using System.Runtime.InteropServices;
using Avalonia.Media;

namespace IntelligentScheduler.Desktop.Platform;

/// <summary>Read-only desktop settings. All calls run on the appearance worker, never the UI.</summary>
internal static class LinuxAppearanceSettings
{
    public static Color? PortalAccent()
    {
        nint connection = 0, reply = 0;
        try
        {
            // Do not spawn a session bus when running CLI tools or headless tests.
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"))) return null;
            connection = Native.g_bus_get_sync(2, 0, out var error);
            if (error != 0) Native.g_error_free(error);
            if (connection == 0) return null;
            var parameters = Native.g_variant_new_tuple([
                Native.g_variant_new_string("org.freedesktop.appearance"),
                Native.g_variant_new_string("accent-color")], 2);
            // Read is supported by older portals too; unwrap both levels of variant below.
            reply = Native.g_dbus_connection_call_sync(connection, "org.freedesktop.portal.Desktop",
                "/org/freedesktop/portal/desktop", "org.freedesktop.portal.Settings", "Read",
                parameters, 0, 0, 700, 0, out error);
            if (error != 0) Native.g_error_free(error);
            if (reply == 0) return null;
            var value = Native.g_variant_get_child_value(reply, 0);
            try
            {
                while (Type(value) == "v")
                {
                    var child = Native.g_variant_get_variant(value);
                    Native.g_variant_unref(value);
                    value = child;
                }
                if (Type(value) != "(ddd)") return null;
                var rgb = new double[3];
                for (var i = 0; i < rgb.Length; i++)
                {
                    var child = Native.g_variant_get_child_value(value, (nuint)i);
                    try { rgb[i] = Native.g_variant_get_double(child); }
                    finally { Native.g_variant_unref(child); }
                }
                return FromRgb(rgb[0], rgb[1], rgb[2]);
            }
            finally { Native.g_variant_unref(value); }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
        finally
        {
            if (reply != 0) Native.g_variant_unref(reply);
            if (connection != 0) Native.g_object_unref(connection);
        }
    }

    internal static Color? FromRgb(double r, double g, double b)
        => double.IsFinite(r) && double.IsFinite(g) && double.IsFinite(b)
            && r is >= 0 and <= 1 && g is >= 0 and <= 1 && b is >= 0 and <= 1
            ? Color.FromRgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255)) : null;

    public static string? Read(string schemaId, string key)
    {
        nint schema = 0, settings = 0, value = 0;
        try
        {
            var source = Native.g_settings_schema_source_get_default();
            if (source == 0) return null;
            schema = Native.g_settings_schema_source_lookup(source, schemaId, 1);
            // g_settings_new would abort the process for a missing schema or key.
            if (schema == 0 || Native.g_settings_schema_has_key(schema, key) == 0) return null;
            settings = Native.g_settings_new_full(schema, 0, 0);
            value = Native.g_settings_get_value(settings, key);
            return value != 0 && Type(value) == "s"
                ? Marshal.PtrToStringUTF8(Native.g_variant_get_string(value, out _)) : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
        finally
        {
            if (value != 0) Native.g_variant_unref(value);
            if (settings != 0) Native.g_object_unref(settings);
            if (schema != 0) Native.g_settings_schema_unref(schema);
        }
    }

    private static string? Type(nint variant) => Marshal.PtrToStringUTF8(Native.g_variant_get_type_string(variant));

    private static class Native
    {
        private const string Gio = "libgio-2.0.so.0", Glib = "libglib-2.0.so.0", GObject = "libgobject-2.0.so.0";
        [DllImport(Gio)] internal static extern nint g_bus_get_sync(int bus, nint cancellable, out nint error);
        [DllImport(Gio)] internal static extern nint g_dbus_connection_call_sync(nint connection,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string bus, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string iface, [MarshalAs(UnmanagedType.LPUTF8Str)] string method,
            nint parameters, nint replyType, int flags, int timeout, nint cancellable, out nint error);
        [DllImport(Gio)] internal static extern nint g_settings_schema_source_get_default();
        [DllImport(Gio)] internal static extern nint g_settings_schema_source_lookup(nint source, [MarshalAs(UnmanagedType.LPUTF8Str)] string id, int recursive);
        [DllImport(Gio)] internal static extern int g_settings_schema_has_key(nint schema, [MarshalAs(UnmanagedType.LPUTF8Str)] string key);
        [DllImport(Gio)] internal static extern nint g_settings_new_full(nint schema, nint backend, nint path);
        [DllImport(Gio)] internal static extern nint g_settings_get_value(nint settings, [MarshalAs(UnmanagedType.LPUTF8Str)] string key);
        [DllImport(Gio)] internal static extern void g_settings_schema_unref(nint schema);
        [DllImport(Glib)] internal static extern nint g_variant_new_string([MarshalAs(UnmanagedType.LPUTF8Str)] string value);
        [DllImport(Glib)] internal static extern nint g_variant_new_tuple([In] nint[] children, nuint count);
        [DllImport(Glib)] internal static extern nint g_variant_get_child_value(nint value, nuint index);
        [DllImport(Glib)] internal static extern nint g_variant_get_variant(nint value);
        [DllImport(Glib)] internal static extern nint g_variant_get_type_string(nint value);
        [DllImport(Glib)] internal static extern nint g_variant_get_string(nint value, out nuint length);
        [DllImport(Glib)] internal static extern double g_variant_get_double(nint value);
        [DllImport(Glib)] internal static extern void g_variant_unref(nint value);
        [DllImport(Glib)] internal static extern void g_error_free(nint error);
        [DllImport(GObject)] internal static extern void g_object_unref(nint value);
    }
}
