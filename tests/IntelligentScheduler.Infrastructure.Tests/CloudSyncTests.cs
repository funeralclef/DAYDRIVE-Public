using System.Net;
using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using IntelligentScheduler.Infrastructure.CloudSync;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IntelligentScheduler.Infrastructure.Tests;

/// <summary>
/// Cloud sync moves a whole database, so the tests care about two things above all: that a
/// round trip preserves the data, and that the service refuses to overwrite anything when it
/// cannot tell which side is ahead.
/// </summary>
public sealed class CloudSyncTests
{
    private static CloudSyncService CreateService(AssistantTestHarness harness)
        => new(harness.ContextFactory, harness.Store, harness.Protector, harness.DatabasePath, "1.2.0");

    private static async Task<AppSettings> UseFolderAsync(AssistantTestHarness harness, string folder,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        return await harness.ConfigureAsync(settings =>
        {
            settings.CloudSyncProvider = CloudSyncProvider.Folder;
            settings.CloudSyncFolderPath = folder;
            settings.CloudSyncDeviceName = "Test machine";
        }, cancellationToken);
    }

    [Fact]
    public async Task AnUnconfiguredFolderIsReportedRatherThanAttempted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        await harness.ConfigureAsync(settings => settings.CloudSyncProvider = CloudSyncProvider.Folder, cancellationToken);

        var report = await CreateService(harness).SyncAsync(CloudSyncDirection.Auto, cancellationToken);

        Assert.Equal(CloudSyncOutcome.NotConfigured, report.Outcome);
    }

    [Fact]
    public async Task SyncIsOffUntilADestinationIsChosen()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);

        var report = await CreateService(harness).SyncAsync(CloudSyncDirection.Auto, cancellationToken);

        Assert.Equal(CloudSyncOutcome.NotConfigured, report.Outcome);
        Assert.Null(await CreateService(harness).PeekAsync(cancellationToken));
    }

    [Fact]
    public async Task TheFirstSyncUploadsAndTheNextOneFindsNothingToDo()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        var folder = Path.Combine(harness.Directory, "cloud");
        await UseFolderAsync(harness, folder, cancellationToken);
        await harness.Store.AddAsync(new TaskItem { Title = "Written before the first upload" }, cancellationToken);

        var service = CreateService(harness);
        var first = await service.SyncAsync(CloudSyncDirection.Auto, cancellationToken);
        Assert.Equal(CloudSyncOutcome.Uploaded, first.Outcome);

        var snapshot = await service.PeekAsync(cancellationToken);
        Assert.NotNull(snapshot);
        Assert.Equal("Test machine", snapshot!.DeviceName);
        Assert.True(snapshot.SizeBytes > 0);

        // Nothing changed on either side, so a second attempt must not upload again.
        var second = await service.SyncAsync(CloudSyncDirection.Auto, cancellationToken);
        Assert.Equal(CloudSyncOutcome.UpToDate, second.Outcome);
    }

    [Fact]
    public async Task ALocalEditAfterAnUploadIsPushedRatherThanTreatedAsAConflict()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        await UseFolderAsync(harness, Path.Combine(harness.Directory, "cloud"), cancellationToken);

        var service = CreateService(harness);
        await service.SyncAsync(CloudSyncDirection.Auto, cancellationToken);

        await harness.Store.AddAsync(new TaskItem { Title = "Added after the first upload" }, cancellationToken);
        var report = await service.SyncAsync(CloudSyncDirection.Auto, cancellationToken);

        Assert.Equal(CloudSyncOutcome.Uploaded, report.Outcome);
    }

    [Fact]
    public async Task ADownloadIsStagedAndOnlySwappedInOnTheNextLaunch()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var source = await AssistantTestHarness.CreateAsync(cancellationToken);
        await using var target = await AssistantTestHarness.CreateAsync(cancellationToken);
        var folder = Path.Combine(source.Directory, "cloud");

        await UseFolderAsync(source, folder, cancellationToken);
        await source.Store.AddAsync(new TaskItem { Title = "Only on the first machine" }, cancellationToken);
        Assert.Equal(CloudSyncOutcome.Uploaded,
            (await CreateService(source).SyncAsync(CloudSyncDirection.Auto, cancellationToken)).Outcome);

        await UseFolderAsync(target, folder, cancellationToken);
        var report = await CreateService(target).SyncAsync(CloudSyncDirection.Download, cancellationToken);

        Assert.Equal(CloudSyncOutcome.Downloaded, report.Outcome);
        Assert.True(report.RequiresRestart);
        // The live database is untouched until the swap; nothing may change under an open connection.
        Assert.DoesNotContain(await target.Store.GetAllAsync(cancellationToken),
            item => item.Title == "Only on the first machine");
        Assert.True(File.Exists(target.DatabasePath + CloudSyncService.PendingRestoreSuffix));

        CloudSyncService.ApplyPendingRestore(target.DatabasePath);
        Assert.False(File.Exists(target.DatabasePath + CloudSyncService.PendingRestoreSuffix));

        // Reading through a fresh context proves the swapped file is a working database.
        await using var reopened = await target.ContextFactory.CreateDbContextAsync(cancellationToken);
        Assert.Contains(reopened.Tasks, item => item.Title == "Only on the first machine");
    }

    [Fact]
    public async Task BothSidesMovingIsReportedAsAConflictInsteadOfPickingOne()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var source = await AssistantTestHarness.CreateAsync(cancellationToken);
        await using var target = await AssistantTestHarness.CreateAsync(cancellationToken);
        var folder = Path.Combine(source.Directory, "cloud");

        // The second machine uploads first, so the folder holds a revision it knows about.
        await UseFolderAsync(target, folder, cancellationToken);
        await CreateService(target).SyncAsync(CloudSyncDirection.Auto, cancellationToken);

        // Then the first machine overwrites it, which the second machine has never seen.
        await UseFolderAsync(source, folder, cancellationToken);
        await source.Store.AddAsync(new TaskItem { Title = "Elsewhere" }, cancellationToken);
        await CreateService(source).SyncAsync(CloudSyncDirection.Upload, cancellationToken);

        // Meanwhile the second machine has work of its own.
        await target.Store.AddAsync(new TaskItem { Title = "Here" }, cancellationToken);
        var report = await CreateService(target).SyncAsync(CloudSyncDirection.Auto, cancellationToken);

        Assert.Equal(CloudSyncOutcome.Conflict, report.Outcome);
        Assert.False(File.Exists(target.DatabasePath + CloudSyncService.PendingRestoreSuffix));
        Assert.Contains(await target.Store.GetAllAsync(cancellationToken), item => item.Title == "Here");
    }

    [Fact]
    public async Task AnExplicitDirectionResolvesTheConflictWithoutAsking()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var source = await AssistantTestHarness.CreateAsync(cancellationToken);
        await using var target = await AssistantTestHarness.CreateAsync(cancellationToken);
        var folder = Path.Combine(source.Directory, "cloud");

        await UseFolderAsync(target, folder, cancellationToken);
        await CreateService(target).SyncAsync(CloudSyncDirection.Auto, cancellationToken);
        await UseFolderAsync(source, folder, cancellationToken);
        await CreateService(source).SyncAsync(CloudSyncDirection.Upload, cancellationToken);
        await target.Store.AddAsync(new TaskItem { Title = "Keep this one" }, cancellationToken);

        var report = await CreateService(target).SyncAsync(CloudSyncDirection.Upload, cancellationToken);

        Assert.Equal(CloudSyncOutcome.Uploaded, report.Outcome);
    }

    [Fact]
    public async Task ANonDatabasePayloadIsRefusedInsteadOfStaged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        var folder = Path.Combine(harness.Directory, "cloud");
        await UseFolderAsync(harness, folder, cancellationToken);

        var settings = await harness.Store.GetAppSettingsAsync(cancellationToken);
        var backend = new FolderSyncBackend();
        // Gzipped, so it decompresses cleanly, but the bytes are not a SQLite file.
        using var payload = new MemoryStream();
        await using (var gzip = new System.IO.Compression.GZipStream(payload,
                         System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            await gzip.WriteAsync("definitely not a database"u8.ToArray(), cancellationToken);
        payload.Position = 0;
        await backend.UploadAsync(settings, payload,
            new CloudSnapshot(string.Empty, DateTimeOffset.UtcNow, 0, "Other", "1.2.0"), cancellationToken);

        var report = await CreateService(harness).SyncAsync(CloudSyncDirection.Download, cancellationToken);

        Assert.Equal(CloudSyncOutcome.Failed, report.Outcome);
        Assert.False(File.Exists(harness.DatabasePath + CloudSyncService.PendingRestoreSuffix));
    }

    [Fact]
    public async Task EachUploadGetsItsOwnRevision()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        var folder = Path.Combine(harness.Directory, "cloud");
        var settings = await UseFolderAsync(harness, folder, cancellationToken);
        var backend = new FolderSyncBackend();

        Assert.True((await backend.CheckReadyAsync(settings, cancellationToken)).IsReady);
        Assert.Null(await backend.GetSnapshotAsync(settings, cancellationToken));

        var metadata = new CloudSnapshot(string.Empty, DateTimeOffset.UtcNow, 0, "Machine", "1.2.0");
        using var first = new MemoryStream("first"u8.ToArray());
        var one = await backend.UploadAsync(settings, first, metadata, cancellationToken);
        using var second = new MemoryStream("second"u8.ToArray());
        var two = await backend.UploadAsync(settings, second, metadata, cancellationToken);

        Assert.NotEqual(one.Revision, two.Revision);
        await using var stored = await backend.DownloadAsync(settings, cancellationToken);
        using var reader = new StreamReader(stored);
        Assert.Equal("second", await reader.ReadToEndAsync(cancellationToken));
    }

    [Fact]
    public void ApplyingAPendingRestoreWithNothingStagedIsANoOp()
        => Assert.Null(CloudSyncService.ApplyPendingRestore(
            Path.Combine(Path.GetTempPath(), $"daydrive-missing-{Guid.NewGuid():N}.db")));

    [Fact]
    public async Task AWholeSettingsSaveNoLongerErasesAGoogleSignIn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        // The shell's copy, loaded before the sign-in happened — exactly what it holds while
        // the browser is open.
        var stale = await harness.Store.GetAppSettingsAsync(cancellationToken);

        await harness.Store.PatchAppSettingsAsync(stored =>
        {
            stored.GoogleDriveRefreshTokenCiphertext = harness.Protector.Protect("refresh-token");
            stored.GoogleDriveAccount = "someone@example.com";
            stored.CloudSyncLastRevision = "rev-1";
        }, cancellationToken);

        // Any unrelated preference change saves the whole stale object.
        stale.ShellThemeMode = ShellThemeMode.Light;
        await harness.Store.UpdateAppSettingsAsync(stale, cancellationToken);

        var stored = await harness.Store.GetAppSettingsAsync(cancellationToken);
        Assert.Equal("refresh-token", harness.Protector.Unprotect(stored.GoogleDriveRefreshTokenCiphertext!));
        Assert.Equal("someone@example.com", stored.GoogleDriveAccount);
        Assert.Equal("rev-1", stored.CloudSyncLastRevision);
        Assert.Equal(ShellThemeMode.Light, stored.ShellThemeMode);
    }

    [Fact]
    public async Task GoogleSetupNamesWhatIsMissingBeforeTouchingTheNetwork()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        var service = CreateService(harness);

        await harness.ConfigureAsync(settings => settings.CloudSyncProvider = CloudSyncProvider.GoogleDrive, cancellationToken);
        Assert.Equal(CloudSyncIssue.GoogleClientIdMissing, (await service.CheckReadyAsync(cancellationToken)).Issue);

        await harness.ConfigureAsync(settings => settings.GoogleDriveClientId = "123-abc.apps.googleusercontent.com",
            cancellationToken);
        Assert.Equal(CloudSyncIssue.GoogleClientSecretMissing, (await service.CheckReadyAsync(cancellationToken)).Issue);

        await harness.ConfigureAsync(settings =>
            settings.GoogleDriveClientSecretCiphertext = harness.Protector.Protect("GOCSPX-secret"), cancellationToken);
        var report = await service.SyncAsync(CloudSyncDirection.Auto, cancellationToken);
        Assert.Equal(CloudSyncOutcome.NotConfigured, report.Outcome);
        Assert.Equal(CloudSyncIssue.GoogleNotConnected, report.Issue);
        Assert.Equal(CloudSyncIssue.GoogleNotConnected, (await service.GetStatusAsync(cancellationToken)).SetupIssue);
    }

    [Fact]
    public async Task ADeletionOnItsOwnIsAChangeWorthUploading()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        await UseFolderAsync(harness, Path.Combine(harness.Directory, "cloud"), cancellationToken);
        await harness.Store.AddAsync(new TaskItem { Title = "Deleted after the upload" }, cancellationToken);
        await harness.Store.AddAsync(new TaskItem { Title = "Stays" }, cancellationToken);

        var service = CreateService(harness);
        Assert.Equal(CloudSyncOutcome.Uploaded, (await service.SyncAsync(CloudSyncDirection.Auto, cancellationToken)).Outcome);
        Assert.False((await service.GetStatusAsync(cancellationToken)).HasLocalChanges);

        await using (var db = await harness.ContextFactory.CreateDbContextAsync(cancellationToken))
        {
            db.Tasks.Remove(await db.Tasks.SingleAsync(item => item.Title == "Deleted after the upload", cancellationToken));
            await db.SaveChangesAsync(cancellationToken);
        }

        // Nothing got newer, only fewer. Stamps alone called this "in sync", and the next
        // download from another machine brought the task back.
        Assert.True((await service.GetStatusAsync(cancellationToken)).HasLocalChanges);
        Assert.Equal(CloudSyncOutcome.Uploaded, (await service.SyncAsync(CloudSyncDirection.Auto, cancellationToken)).Outcome);
    }

    [Fact]
    public async Task ARestoredCopyKeepsThisMachinesSettingsAndIsInSyncAfterwards()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var source = await AssistantTestHarness.CreateAsync(cancellationToken);
        await using var target = await AssistantTestHarness.CreateAsync(cancellationToken);
        var folder = Path.Combine(source.Directory, "cloud");

        await UseFolderAsync(source, folder, cancellationToken);
        await source.ConfigureAsync(settings => settings.CloudSyncDeviceName = "Source machine", cancellationToken);
        await source.Store.PatchAppSettingsAsync(settings => settings.GoogleDriveAccount = "source@example.com", cancellationToken);
        await source.Store.AddAsync(new TaskItem { Title = "Made on the source" }, cancellationToken);
        Assert.Equal(CloudSyncOutcome.Uploaded,
            (await CreateService(source).SyncAsync(CloudSyncDirection.Auto, cancellationToken)).Outcome);

        await UseFolderAsync(target, folder, cancellationToken);
        await target.ConfigureAsync(settings =>
        {
            settings.CloudSyncDeviceName = "Target machine";
            settings.ShellThemeMode = ShellThemeMode.Light;
        }, cancellationToken);
        var targetSignIn = target.Protector.Protect("target-refresh-token");
        await target.Store.PatchAppSettingsAsync(settings => settings.GoogleDriveRefreshTokenCiphertext = targetSignIn,
            cancellationToken);
        Assert.Equal(CloudSyncOutcome.Downloaded,
            (await CreateService(target).SyncAsync(CloudSyncDirection.Download, cancellationToken)).Outcome);

        var applied = CloudSyncService.ApplyPendingRestore(target.DatabasePath);
        Assert.True(applied is not null,
            string.Join(" | ", CreateService(target).History.Select(entry => entry.Detail)));

        var restored = await target.Store.GetAppSettingsAsync(cancellationToken);
        Assert.Equal("Target machine", restored.CloudSyncDeviceName);
        Assert.Equal(ShellTheme.Marelian, restored.ShellTheme);
        Assert.Equal(ShellThemeMode.Light, restored.ShellThemeMode);
        Assert.Equal(targetSignIn, restored.GoogleDriveRefreshTokenCiphertext);
        Assert.Equal(string.Empty, restored.GoogleDriveAccount);
        Assert.Contains(await target.Store.GetAllAsync(cancellationToken), item => item.Title == "Made on the source");

        // The copy it just took is what the cloud holds, so there is nothing to do. This used to
        // be a conflict: the other machine's bookkeeping came along with its data.
        var next = await CreateService(target).SyncAsync(CloudSyncDirection.Auto, cancellationToken);
        Assert.Equal(CloudSyncOutcome.UpToDate, next.Outcome);
        Assert.Contains(CreateService(target).History, entry => entry.Outcome == CloudSyncOutcome.Applied);
    }

    [Fact]
    public async Task TheJournalFoldsRepeatedChecksIntoOneLine()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        await UseFolderAsync(harness, Path.Combine(harness.Directory, "cloud"), cancellationToken);
        await harness.Store.AddAsync(new TaskItem { Title = "Something to upload" }, cancellationToken);

        var service = CreateService(harness);
        await service.SyncAsync(CloudSyncDirection.Auto, CloudSyncTrigger.Manual, cancellationToken);
        await service.SyncAsync(CloudSyncDirection.Auto, CloudSyncTrigger.Automatic, cancellationToken);
        await service.SyncAsync(CloudSyncDirection.Auto, CloudSyncTrigger.Automatic, cancellationToken);

        // Read back by a fresh instance, so this is what survives a restart.
        var journal = CreateService(harness).History;
        Assert.Equal([CloudSyncOutcome.Uploaded, CloudSyncOutcome.UpToDate], journal.Select(entry => entry.Outcome));
        Assert.True(journal[0].Bytes > 0);
        Assert.Equal("Test machine", journal[0].Subject);
        Assert.Equal(CloudSyncTrigger.Automatic, journal[^1].Trigger);

        var status = await service.GetStatusAsync(cancellationToken);
        Assert.Equal(CloudSyncOutcome.UpToDate, status.LastSuccess?.Outcome);
        Assert.Equal(CloudSyncIssue.None, status.SetupIssue);
    }

    [Fact]
    public void GoogleRefusalsAreNamedRatherThanPassedOn()
    {
        Assert.Equal(CloudSyncIssue.GoogleClientSecretMissing, GoogleDriveSyncBackend.ClassifyTokenError(
            """{"error":"invalid_request","error_description":"client_secret is missing."}""",
            HttpStatusCode.BadRequest).Issue);
        Assert.Equal(CloudSyncIssue.GoogleReconnect, GoogleDriveSyncBackend.ClassifyTokenError(
            """{"error":"invalid_grant","error_description":"Token has been expired or revoked."}""",
            HttpStatusCode.BadRequest).Issue);
        Assert.Equal(CloudSyncIssue.GoogleClientRejected, GoogleDriveSyncBackend.ClassifyTokenError(
            """{"error":"invalid_client","error_description":"The OAuth client was not found."}""",
            HttpStatusCode.Unauthorized).Issue);
        Assert.Equal(CloudSyncIssue.GoogleDriveApiDisabled, GoogleDriveSyncBackend.ClassifyDriveError(
            """{"error":{"code":403,"message":"Google Drive API has not been used in project 1 before or it is disabled.","errors":[{"reason":"accessNotConfigured"}],"status":"PERMISSION_DENIED"}}""",
            HttpStatusCode.Forbidden).Issue);
        Assert.Equal(CloudSyncIssue.GoogleScopeMissing, GoogleDriveSyncBackend.ClassifyDriveError(
            """{"error":{"code":403,"message":"Request had insufficient authentication scopes.","errors":[{"reason":"insufficientPermissions"}],"status":"PERMISSION_DENIED"}}""",
            HttpStatusCode.Forbidden).Issue);
        Assert.Equal(CloudSyncIssue.Unexpected, GoogleDriveSyncBackend.ClassifyDriveError(
            "not json at all", HttpStatusCode.InternalServerError).Issue);
    }

    [Fact]
    public async Task TheSameFailureOverAndOverIsOneLineOfHistory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        await harness.ConfigureAsync(settings => settings.CloudSyncProvider = CloudSyncProvider.GoogleDrive, cancellationToken);

        var service = CreateService(harness);
        for (var attempt = 0; attempt < 3; attempt++)
            await service.SyncAsync(CloudSyncDirection.Auto, CloudSyncTrigger.Automatic, cancellationToken);

        var entry = Assert.Single(service.History);
        Assert.Equal(CloudSyncIssue.GoogleClientIdMissing, entry.Issue);
    }
}
