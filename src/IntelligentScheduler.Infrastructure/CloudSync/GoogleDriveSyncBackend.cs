using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;

namespace IntelligentScheduler.Infrastructure.CloudSync;

/// <summary>Everything the consent flow produced, ready to be persisted.</summary>
/// <param name="Problem">
/// A fault found after the sign-in itself succeeded — typically the Drive API left switched off
/// in the user's project. The sign-in is still worth keeping: fixing the project does not need
/// a second one.
/// </param>
public sealed record GoogleAuthorization(string RefreshToken, string Account, CloudSyncException? Problem = null);

/// <summary>
/// Google Drive through the application data folder.
///
/// Two deliberate choices. First, the Drive scope is <c>drive.appdata</c> only: the app can read
/// and write the hidden folder Drive gives it and nothing else in the user's Drive, so a bug here
/// cannot touch their documents. Second, there is no client id baked into the build — this is
/// an open repository, and an embedded desktop client would be shared by every user of it.
/// The user registers a Desktop client in their own Google Cloud project and pastes its id and
/// secret.
/// </summary>
public sealed class GoogleDriveSyncBackend(ISecretProtector secrets) : ICloudSyncBackend, IDisposable
{
    private const string FileName = "daydrive-snapshot.db.gz";
    private const string DriveScope = "https://www.googleapis.com/auth/drive.appdata";
    // openid and email only put a name to the account on the settings page.
    private const string Scope = "openid email " + DriveScope;
    private const string AuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string FilesEndpoint = "https://www.googleapis.com/drive/v3/files";
    private const string UploadEndpoint = "https://www.googleapis.com/upload/drive/v3/files";
    private const string AboutEndpoint = "https://www.googleapis.com/drive/v3/about?fields=user(emailAddress)";
    private static readonly TimeSpan SignInWindow = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiry = DateTimeOffset.MinValue;

    public CloudSyncProvider Kind => CloudSyncProvider.GoogleDrive;

    /// <summary>Where the user gets a client id. Shown next to the field in settings.</summary>
    public const string ConsoleUrl = "https://console.cloud.google.com/apis/credentials";

    public CloudSyncIssue CheckSetup(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.GoogleDriveClientId)) return CloudSyncIssue.GoogleClientIdMissing;
        // An account that is already connected got past the secret once; asking again would only
        // stand in the way of a client type that has none.
        if (!string.IsNullOrWhiteSpace(settings.GoogleDriveRefreshTokenCiphertext)) return CloudSyncIssue.None;
        return string.IsNullOrWhiteSpace(settings.GoogleDriveClientSecretCiphertext)
            ? CloudSyncIssue.GoogleClientSecretMissing
            : CloudSyncIssue.GoogleNotConnected;
    }

    public async Task<CloudReadiness> CheckReadyAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var setup = CheckSetup(settings);
        if (setup != CloudSyncIssue.None)
            return CloudReadiness.NotReady(setup, setup switch
            {
                CloudSyncIssue.GoogleClientIdMissing => "Paste the OAuth client id from your Google Cloud project.",
                CloudSyncIssue.GoogleClientSecretMissing => "Paste the OAuth client secret from your Google Cloud project.",
                _ => "Connect the Google account to finish setup."
            });
        try
        {
            await EnsureAccessTokenAsync(settings, cancellationToken);
            return CloudReadiness.Ready(string.IsNullOrWhiteSpace(settings.GoogleDriveAccount)
                ? "Google Drive connected."
                : $"Connected as {settings.GoogleDriveAccount}.");
        }
        catch (CloudSyncException exception)
        {
            return CloudReadiness.NotReady(exception.Issue, exception.Message);
        }
    }

    // ---------------------------------------------------------------- consent

    /// <summary>
    /// Runs the installed-application flow: PKCE, a loopback listener the browser redirects to,
    /// and a code exchange. Nothing is typed into the app — the password stays with Google.
    ///
    /// The browser tab is held open until the exchange is done, so the page it finally shows is
    /// the real outcome. It used to say "connected" the moment the code arrived, before the
    /// exchange that could still fail — and a failed exchange then stored nothing, leaving the
    /// app to ask the user to connect an account they had just watched it connect.
    /// </summary>
    public async Task<GoogleAuthorization> AuthorizeAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var clientId = settings.GoogleDriveClientId?.Trim();
        if (string.IsNullOrWhiteSpace(clientId))
            throw new CloudSyncException(CloudSyncIssue.GoogleClientIdMissing,
                "Paste the OAuth client id from your Google Cloud project first.");
        // Checked before the browser opens: Google asks a Desktop client for its secret during
        // the exchange, and finding that out after the user has signed in is the worst moment.
        if (string.IsNullOrWhiteSpace(ClientSecret(settings)))
            throw new CloudSyncException(CloudSyncIssue.GoogleClientSecretMissing,
                "Paste the OAuth client secret from your Google Cloud project first.");

        var verifier = CreateCodeVerifier();
        var challenge = CreateCodeChallenge(verifier);
        var state = CreateCodeVerifier();

        using var listener = new HttpListener();
        var redirectUri = StartLoopbackListener(listener);
        try
        {
            var authorizeUrl = $"{AuthorizeEndpoint}?" + string.Join('&',
            [
                $"client_id={Uri.EscapeDataString(clientId)}",
                $"redirect_uri={Uri.EscapeDataString(redirectUri)}",
                "response_type=code",
                $"scope={Uri.EscapeDataString(Scope)}",
                $"code_challenge={challenge}",
                "code_challenge_method=S256",
                "access_type=offline",
                // Without this Google reissues a refresh token only on the very first consent.
                "prompt=consent",
                $"state={state}"
            ]);

            OpenBrowser(authorizeUrl);

            // The browser tab is the user's to complete; five minutes is generous but not endless.
            using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            window.CancelAfter(SignInWindow);
            HttpListenerContext callback;
            string code;
            try
            {
                (callback, code) = await WaitForCallbackAsync(listener, state, window.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new CloudSyncException(CloudSyncIssue.SignInTimedOut,
                    "The Google sign-in was not completed within five minutes.");
            }

            try
            {
                var authorization = await ExchangeCodeAsync(settings, clientId, code, verifier, redirectUri, cancellationToken);
                await RespondAsync(callback, authorization.Problem is null
                        ? "DAYDRIVE is connected to Google Drive. You can close this tab."
                        : $"You are signed in, but Google Drive refused access: {authorization.Problem.Message} " +
                          "Go back to DAYDRIVE to see what to do.",
                    success: authorization.Problem is null);
                return authorization;
            }
            catch (Exception exception)
            {
                await RespondAsync(callback,
                    $"DAYDRIVE could not finish connecting. {exception.Message} Go back to DAYDRIVE to see what to do.",
                    success: false);
                throw;
            }
        }
        finally
        {
            if (listener.IsListening) listener.Stop();
        }
    }

    private async Task<GoogleAuthorization> ExchangeCodeAsync(AppSettings settings, string clientId, string code,
        string verifier, string redirectUri, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(BuildTokenForm(settings, clientId, new Dictionary<string, string>
        {
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri
        }));
        var body = await PostTokenAsync(content, cancellationToken);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var refreshToken = Text(root, "refresh_token");
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new CloudSyncException(CloudSyncIssue.GoogleReconnect,
                "Google did not return a refresh token. Remove DAYDRIVE from your Google account's third-party access and connect again.");

        // Google's consent screen lets each permission be unticked on its own. A sign-in without
        // the Drive one can never sync, and saying so now beats a refusal at the first upload.
        var granted = Text(root, "scope");
        if (granted.Length > 0 && !granted.Split(' ').Contains(DriveScope, StringComparer.Ordinal))
            throw new CloudSyncException(CloudSyncIssue.GoogleScopeMissing,
                "The permission to keep DAYDRIVE's own data in Google Drive was not granted.");

        RememberAccessToken(root);
        var account = ReadAccountFromIdToken(Text(root, "id_token"));

        // One cheap call proves the whole path — the Drive API switched on, the scope honoured —
        // while the user is still looking at the result, instead of at the first sync.
        CloudSyncException? problem = null;
        try
        {
            var owner = await ReadDriveOwnerAsync(cancellationToken);
            if (account.Length == 0) account = owner;
        }
        catch (CloudSyncException exception)
        {
            problem = exception;
        }
        return new GoogleAuthorization(refreshToken, account, problem);
    }

    private async Task<string> ReadDriveOwnerAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, AboutEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        using var response = await TransportAsync(
            () => _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw ClassifyDriveError(body, response.StatusCode);
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("user", out var user) ? Text(user, "emailAddress") : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    public void ForgetTokens()
    {
        _accessToken = null;
        _accessTokenExpiry = DateTimeOffset.MinValue;
    }

    // ---------------------------------------------------------------- snapshot

    public async Task<CloudSnapshot?> GetSnapshotAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var file = await FindFileAsync(settings, cancellationToken);
        return file is null ? null : ToSnapshot(file.Value);
    }

    public async Task<CloudSnapshot> UploadAsync(AppSettings settings, Stream payload, CloudSnapshot metadata,
        CancellationToken cancellationToken = default)
    {
        var existing = await FindFileAsync(settings, cancellationToken);
        var descriptor = new
        {
            name = FileName,
            // "parents" is rejected on update; only the create call may place the file.
            parents = existing is null ? new[] { "appDataFolder" } : null,
            appProperties = new
            {
                device = metadata.DeviceName,
                version = metadata.AppVersion,
                stamp = metadata.ModifiedUtc.ToUnixTimeMilliseconds().ToString()
            }
        };

        using var multipart = new MultipartContent("related");
        var metadataContent = new StringContent(
            JsonSerializer.Serialize(descriptor, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            }), Encoding.UTF8);
        metadataContent.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "UTF-8" };
        multipart.Add(metadataContent);

        var payloadContent = new StreamContent(payload);
        payloadContent.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
        multipart.Add(payloadContent);

        var url = existing is null
            ? $"{UploadEndpoint}?uploadType=multipart&fields={Uri.EscapeDataString(FileFields)}"
            : $"{UploadEndpoint}/{existing.Value.Id}?uploadType=multipart&fields={Uri.EscapeDataString(FileFields)}";

        using var request = new HttpRequestMessage(existing is null ? HttpMethod.Post : HttpMethod.Patch, url)
        {
            Content = multipart
        };
        using var response = await SendAsync(settings, request, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var uploaded = ReadFile(document.RootElement)
                       ?? throw new CloudSyncException(CloudSyncIssue.Unexpected,
                           "Google Drive accepted the upload but returned no file.");
        return ToSnapshot(uploaded);
    }

    public async Task<Stream> DownloadAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var file = await FindFileAsync(settings, cancellationToken)
                   ?? throw new CloudSyncException(CloudSyncIssue.NothingInCloud,
                       "No snapshot has been uploaded to Google Drive yet.");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{FilesEndpoint}/{file.Id}?alt=media");
        var response = await SendAsync(settings, request, cancellationToken);
        try
        {
            // Buffered on purpose: the caller replaces a database with it, and a stream that
            // dies mid-copy would leave a truncated file behind.
            var buffer = new MemoryStream();
            await TransportAsync(async () => { await response.Content.CopyToAsync(buffer, cancellationToken); return response; },
                cancellationToken);
            buffer.Position = 0;
            return buffer;
        }
        finally { response.Dispose(); }
    }

    private const string FileFields = "id,name,modifiedTime,size,md5Checksum,appProperties";

    private async Task<DriveFile?> FindFileAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var query = Uri.EscapeDataString($"name = '{FileName}' and trashed = false");
        var url = $"{FilesEndpoint}?spaces=appDataFolder&pageSize=10" +
                  $"&q={query}&fields={Uri.EscapeDataString($"files({FileFields})")}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync(settings, request, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            return null;

        DriveFile? newest = null;
        foreach (var element in files.EnumerateArray())
        {
            var file = ReadFile(element);
            if (file is null) continue;
            if (newest is null || file.Value.ModifiedUtc > newest.Value.ModifiedUtc) newest = file;
        }
        return newest;
    }

    private static DriveFile? ReadFile(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var id = Text(element, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;

        var modified = DateTimeOffset.TryParse(Text(element, "modifiedTime"), out var parsed)
            ? parsed.ToUniversalTime()
            : DateTimeOffset.UtcNow;
        var size = long.TryParse(Text(element, "size"), out var bytes) ? bytes : 0;
        var checksum = Text(element, "md5Checksum");

        var device = string.Empty;
        var version = string.Empty;
        if (element.TryGetProperty("appProperties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            device = Text(properties, "device");
            version = Text(properties, "version");
        }

        return new DriveFile(id, modified, size,
            // The checksum changes with every distinct upload; the timestamp is the fallback.
            string.IsNullOrWhiteSpace(checksum) ? modified.ToUnixTimeMilliseconds().ToString() : checksum,
            device, version);
    }

    private static CloudSnapshot ToSnapshot(DriveFile file)
        => new(file.Revision, file.ModifiedUtc, file.SizeBytes, file.DeviceName, file.AppVersion);

    // ---------------------------------------------------------------- transport

    /// <summary>
    /// Sends with a fresh access token. A request without a body is retried once when Google
    /// says the token expired; an upload is not, because its stream has already been read.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(AppSettings settings, HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = await EnsureAccessTokenAsync(settings, cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await TransportAsync(
            () => _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken), cancellationToken);
        if (response.IsSuccessStatusCode) return response;

        var status = response.StatusCode;
        var error = await response.Content.ReadAsStringAsync(cancellationToken);
        response.Dispose();
        if (status != HttpStatusCode.Unauthorized) throw ClassifyDriveError(error, status);

        _accessTokenExpiry = DateTimeOffset.MinValue;
        if (request.Content is not null)
            throw new CloudSyncException(CloudSyncIssue.Unexpected,
                "Google Drive turned the upload's sign-in down. The next attempt signs in afresh.");

        // A retry needs its own message: a sent HttpRequestMessage cannot be reused.
        using var retry = new HttpRequestMessage(request.Method, request.RequestUri);
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            await EnsureAccessTokenAsync(settings, cancellationToken));
        var second = await TransportAsync(
            () => _http.SendAsync(retry, HttpCompletionOption.ResponseHeadersRead, cancellationToken), cancellationToken);
        if (second.IsSuccessStatusCode) return second;
        var secondError = await second.Content.ReadAsStringAsync(cancellationToken);
        var secondStatus = second.StatusCode;
        second.Dispose();
        throw ClassifyDriveError(secondError, secondStatus);
    }

    private async Task<string> EnsureAccessTokenAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (_accessToken is { Length: > 0 } && DateTimeOffset.UtcNow < _accessTokenExpiry) return _accessToken;

        var clientId = settings.GoogleDriveClientId?.Trim();
        if (string.IsNullOrWhiteSpace(clientId))
            throw new CloudSyncException(CloudSyncIssue.GoogleClientIdMissing,
                "Paste the OAuth client id from your Google Cloud project first.");
        if (string.IsNullOrWhiteSpace(settings.GoogleDriveRefreshTokenCiphertext))
            throw new CloudSyncException(CloudSyncIssue.GoogleNotConnected, "Connect the Google account first.");

        var refreshToken = secrets.Unprotect(settings.GoogleDriveRefreshTokenCiphertext);
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new CloudSyncException(CloudSyncIssue.GoogleReconnect,
                "The stored Google sign-in cannot be read on this computer. Connect the account again.");

        using var content = new FormUrlEncodedContent(BuildTokenForm(settings, clientId, new Dictionary<string, string>
        {
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token"
        }));
        var body = await PostTokenAsync(content, cancellationToken);
        using var document = JsonDocument.Parse(body);
        RememberAccessToken(document.RootElement);
        if (string.IsNullOrWhiteSpace(_accessToken))
            throw new CloudSyncException(CloudSyncIssue.Unexpected, "Google returned no access token.");
        return _accessToken;
    }

    private async Task<string> PostTokenAsync(HttpContent content, CancellationToken cancellationToken)
    {
        using var response = await TransportAsync(() => _http.PostAsync(TokenEndpoint, content, cancellationToken),
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw ClassifyTokenError(body, response.StatusCode);
        return body;
    }

    private void RememberAccessToken(JsonElement root)
    {
        _accessToken = Text(root, "access_token");
        _accessTokenExpiry = DateTimeOffset.UtcNow.AddSeconds(
            root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds)
                ? Math.Max(60, seconds - 60)
                : 300);
    }

    /// <summary>
    /// A connection that never arrived is its own kind of failure: nothing is wrong with the
    /// setup, and the next automatic attempt will very likely just work.
    /// </summary>
    private static async Task<HttpResponseMessage> TransportAsync(Func<Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken)
    {
        try
        {
            return await send();
        }
        catch (HttpRequestException exception)
        {
            throw new CloudSyncException(CloudSyncIssue.Offline, exception.Message, exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CloudSyncException(CloudSyncIssue.Offline, "Google did not answer in time.", exception);
        }
    }

    private Dictionary<string, string> BuildTokenForm(AppSettings settings, string clientId, Dictionary<string, string> fields)
    {
        fields["client_id"] = clientId;
        // Google issues desktop clients a secret and still expects it here. It is not treated
        // as confidential for installed apps, but it is the user's, so it stays protected on disk.
        var secret = ClientSecret(settings);
        if (!string.IsNullOrWhiteSpace(secret)) fields["client_secret"] = secret;
        return fields;
    }

    private string ClientSecret(AppSettings settings)
        => string.IsNullOrWhiteSpace(settings.GoogleDriveClientSecretCiphertext)
            ? string.Empty
            : secrets.Unprotect(settings.GoogleDriveClientSecretCiphertext);

    // ---------------------------------------------------------------- errors

    /// <summary>Names a refusal from the token endpoint.</summary>
    public static CloudSyncException ClassifyTokenError(string body, HttpStatusCode status)
    {
        var error = string.Empty;
        var description = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(body);
            error = Text(document.RootElement, "error");
            description = Text(document.RootElement, "error_description");
        }
        catch (JsonException)
        {
            // Non-JSON body; the status line is the only thing left to report.
        }

        var detail = description.Length > 0 ? $"Google: {description}"
            : error.Length > 0 ? $"Google: {error}"
            : $"Google answered {(int)status}.";
        if (description.Contains("client_secret", StringComparison.OrdinalIgnoreCase))
            return new CloudSyncException(CloudSyncIssue.GoogleClientSecretMissing, detail);
        return error switch
        {
            "invalid_client" or "unauthorized_client" => new CloudSyncException(CloudSyncIssue.GoogleClientRejected, detail),
            "invalid_grant" => new CloudSyncException(CloudSyncIssue.GoogleReconnect, detail),
            _ => new CloudSyncException(CloudSyncIssue.Unexpected, detail)
        };
    }

    /// <summary>Names a refusal from the Drive API.</summary>
    public static CloudSyncException ClassifyDriveError(string body, HttpStatusCode status)
    {
        var message = string.Empty;
        var reasons = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                message = Text(error, "message");
                reasons.Add(Text(error, "status"));
                foreach (var list in new[] { "errors", "details" })
                    if (error.TryGetProperty(list, out var items) && items.ValueKind == JsonValueKind.Array)
                        reasons.AddRange(items.EnumerateArray().Select(item => Text(item, "reason")));
            }
        }
        catch (JsonException)
        {
            // Fall through to the status line.
        }

        var detail = message.Length > 0 ? $"Google Drive: {message}" : $"Google Drive answered {(int)status}.";
        if (reasons.Any(reason => reason is "accessNotConfigured" or "SERVICE_DISABLED") ||
            message.Contains("has not been used", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("is disabled", StringComparison.OrdinalIgnoreCase))
            return new CloudSyncException(CloudSyncIssue.GoogleDriveApiDisabled, detail);
        if (reasons.Any(reason => reason is "insufficientPermissions" or "ACCESS_TOKEN_SCOPE_INSUFFICIENT") ||
            message.Contains("insufficient authentication scopes", StringComparison.OrdinalIgnoreCase))
            return new CloudSyncException(CloudSyncIssue.GoogleScopeMissing, detail);
        return new CloudSyncException(CloudSyncIssue.Unexpected, detail);
    }

    // ---------------------------------------------------------------- loopback

    private static string StartLoopbackListener(HttpListener listener)
    {
        // Google allows any port on 127.0.0.1 for installed apps, so a free one is picked at run time.
        foreach (var port in FreePorts())
        {
            var prefix = $"http://127.0.0.1:{port}/";
            try
            {
                listener.Prefixes.Clear();
                listener.Prefixes.Add(prefix);
                listener.Start();
                return prefix.TrimEnd('/');
            }
            catch (HttpListenerException)
            {
                // Taken between the probe and the bind; try the next one.
            }
        }
        throw new CloudSyncException(CloudSyncIssue.Unexpected, "No local port was available for the Google sign-in redirect.");
    }

    private static IEnumerable<int> FreePorts()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            yield return port;
        }
    }

    /// <summary>
    /// Waits for the browser to come back with a verdict. The request that carries the code is
    /// handed back unanswered: the page is only written once the exchange has an outcome.
    /// </summary>
    private static async Task<(HttpListenerContext Context, string Code)> WaitForCallbackAsync(
        HttpListener listener, string expectedState, CancellationToken cancellationToken)
    {
        while (true)
        {
            var contextTask = listener.GetContextAsync();
            var completed = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, cancellationToken));
            if (completed != contextTask)
            {
                // The listener is stopped by the caller, which faults this task; observe it here.
                _ = contextTask.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                cancellationToken.ThrowIfCancellationRequested();
            }

            var context = await contextTask;
            var query = context.Request.QueryString;
            var code = query["code"];
            var error = query["error"];
            var state = query["state"];

            // Favicon and similar stray requests hit the same listener; ignore anything without a verdict.
            if (string.IsNullOrEmpty(code) && string.IsNullOrEmpty(error))
            {
                await RespondAsync(context, "You can close this tab.", success: true);
                continue;
            }

            if (!string.IsNullOrEmpty(error))
            {
                await RespondAsync(context, "Sign-in was cancelled. You can close this tab.", success: false);
                throw error == "access_denied"
                    ? new CloudSyncException(CloudSyncIssue.SignInCancelled, "Access was not granted in the browser.")
                    : new CloudSyncException(CloudSyncIssue.Unexpected, $"Google reported \"{error}\".");
            }

            if (!string.Equals(state, expectedState, StringComparison.Ordinal))
            {
                await RespondAsync(context, "This response did not match the request. You can close this tab.", success: false);
                throw new CloudSyncException(CloudSyncIssue.Unexpected, "The Google sign-in response did not match the request.");
            }

            return (context, code!);
        }
    }

    private static async Task RespondAsync(HttpListenerContext context, string message, bool success)
    {
        try
        {
            var mark = success ? "#55D6A1" : "#FF8295";
            var body = Encoding.UTF8.GetBytes(
                "<!doctype html><html><head><meta charset=\"utf-8\"><title>DAYDRIVE</title></head>" +
                "<body style=\"font-family:system-ui,sans-serif;display:flex;align-items:center;" +
                "justify-content:center;height:100vh;margin:0;background:#13110F;color:#F5F2EE\">" +
                $"<div style=\"max-width:520px;padding:24px;border-left:4px solid {mark}\">" +
                $"<p style=\"font-size:16px;line-height:1.5;margin:0\">{WebUtility.HtmlEncode(message)}</p></div></body></html>");
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }
        catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or IOException)
        {
            // The tab was closed first; the app still reports the outcome.
        }
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            throw new CloudSyncException(CloudSyncIssue.Unexpected,
                $"The browser could not be opened for sign-in: {exception.Message}");
        }
    }

    // ---------------------------------------------------------------- helpers

    private static string CreateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(48);
        return Base64Url(bytes);
    }

    private static string CreateCodeChallenge(string verifier)
        => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Reads the email claim without validating the token: it is only ever displayed.</summary>
    private static string ReadAccountFromIdToken(string idToken)
    {
        try
        {
            var parts = idToken.Split('.');
            if (parts.Length < 2) return string.Empty;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return Text(document.RootElement, "email");
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string Text(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    public void Dispose() => _http.Dispose();

    private readonly record struct DriveFile(
        string Id, DateTimeOffset ModifiedUtc, long SizeBytes, string Revision, string DeviceName, string AppVersion);
}
