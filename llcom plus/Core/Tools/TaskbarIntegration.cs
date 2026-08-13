using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace llcom_plus.Tools
{
    /// <summary>
    /// Gives the unpackaged executable a stable Windows Shell identity. Without an
    /// explicit identity, taskbar pinning is based on path heuristics and can keep
    /// using an icon cached for an older executable at the same location.
    /// </summary>
    internal static class TaskbarIntegration
    {
        internal const string AppUserModelId = "LawrenceLiu.LlcomPlus";
        internal const string TaskbarIconFileName = "llcom-plus-taskbar-v2.ico";
        internal const string DisplayName = "llcom plus";

        private const int AppModelErrorNoPackage = 15700;
        private const ushort VtBool = 11;
        private const ushort VtLpwstr = 31;
        private const int EmbeddedApplicationIconResourceId = 32512;

        private static readonly Guid AppUserModelPropertySet =
            new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
        private static readonly PropertyKey AppUserModelIdKey =
            new PropertyKey(AppUserModelPropertySet, 5);
        private static readonly PropertyKey PreventPinningKey =
            new PropertyKey(AppUserModelPropertySet, 9);
        private static readonly PropertyKey RelaunchCommandKey =
            new PropertyKey(AppUserModelPropertySet, 2);
        private static readonly PropertyKey RelaunchDisplayNameKey =
            new PropertyKey(AppUserModelPropertySet, 4);
        private static readonly PropertyKey RelaunchIconKey =
            new PropertyKey(AppUserModelPropertySet, 3);

        private static bool processIdentityInitialized;

        /// <summary>
        /// Must run before WPF creates any UI. Packaged builds retain the AppUserModelID
        /// supplied by their MSIX package identity.
        /// </summary>
        internal static bool InitializeProcessIdentity()
        {
            if (IsPackagedProcess())
                return false;

            if (processIdentityInitialized)
                return true;

            try
            {
                var result = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
                processIdentityInitialized = result >= 0;
                StartupProfiler.Mark(
                    "Taskbar process identity result=0x" + result.ToString("X8"));
                return processIdentityInitialized;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            catch (Exception exception)
            {
                StartupProfiler.Mark(
                    "Taskbar process identity failure=" + exception.GetType().Name);
                return false;
            }
        }

        /// <summary>
        /// Supplies all information Windows needs to create a fresh, pinnable taskbar
        /// shortcut. Relaunch properties are honored only with a window-level ID.
        /// </summary>
        internal static bool ConfigureWindow(Window window)
        {
            if (window == null || IsPackagedProcess())
                return false;

            InitializeProcessIdentity();
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
                return false;

            IPropertyStore propertyStore = null;
            try
            {
                var interfaceId = typeof(IPropertyStore).GUID;
                var result = SHGetPropertyStoreForWindow(handle, ref interfaceId, out propertyStore);
                if (result < 0 || propertyStore == null)
                {
                    StartupProfiler.Mark(
                        "Taskbar window property store unavailable, result=0x" + result.ToString("X8"));
                    return false;
                }

                var executablePath = GetExecutablePath();
                var applicationDirectory = Path.GetDirectoryName(executablePath) ?? "";

                // PreventPinning must be written before AppUserModel.ID. Explicitly
                // writing false overrides stale Shell classification of the previous
                // path-derived identity.
                SetBoolProperty(propertyStore, PreventPinningKey, false);
                SetStringProperty(propertyStore, RelaunchCommandKey, BuildRelaunchCommand(executablePath));
                SetStringProperty(propertyStore, RelaunchDisplayNameKey, DisplayName);
                SetStringProperty(
                    propertyStore,
                    RelaunchIconKey,
                    BuildRelaunchIconResource(applicationDirectory, executablePath));
                SetStringProperty(propertyStore, AppUserModelIdKey, AppUserModelId);
                var commitResult = propertyStore.Commit();
                var configured = commitResult >= 0 &&
                    GetStringProperty(propertyStore, AppUserModelIdKey) == AppUserModelId &&
                    GetStringProperty(propertyStore, RelaunchCommandKey) == BuildRelaunchCommand(executablePath) &&
                    GetStringProperty(propertyStore, RelaunchDisplayNameKey) == DisplayName &&
                    GetStringProperty(propertyStore, RelaunchIconKey) ==
                        BuildRelaunchIconResource(applicationDirectory, executablePath) &&
                    GetBoolProperty(propertyStore, PreventPinningKey) != true;
                StartupProfiler.Mark(
                    "Taskbar window identity commit result=0x" + commitResult.ToString("X8") +
                    ", verified=" + configured);
                return configured;
            }
            catch (COMException exception)
            {
                StartupProfiler.Mark(
                    "Taskbar window identity COM failure=0x" + exception.ErrorCode.ToString("X8"));
                return false;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            catch (Exception exception)
            {
                StartupProfiler.Mark(
                    "Taskbar window identity failure=" + exception.GetType().Name);
                return false;
            }
            finally
            {
                if (propertyStore != null && Marshal.IsComObject(propertyStore))
                {
                    try
                    {
                        Marshal.FinalReleaseComObject(propertyStore);
                    }
                    catch
                    {
                        // Taskbar integration must never prevent application startup.
                    }
                }
            }
        }

        internal static string BuildRelaunchCommand(string executablePath)
        {
            return "\"" + executablePath + "\"";
        }

        internal static string BuildRelaunchIconResource(string applicationDirectory, string executablePath)
        {
            var taskbarIconPath = Path.Combine(applicationDirectory ?? "", TaskbarIconFileName);
            if (File.Exists(taskbarIconPath))
                return taskbarIconPath + ",0";

            return executablePath + ",-" + EmbeddedApplicationIconResourceId;
        }

        private static string GetExecutablePath()
        {
            using (var process = Process.GetCurrentProcess())
            using (var mainModule = process.MainModule)
                return mainModule?.FileName ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
        }

        private static bool IsPackagedProcess()
        {
            try
            {
                uint length = 0;
                var result = GetCurrentPackageFullName(ref length, null);
                return result != AppModelErrorNoPackage;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }

        private static void SetStringProperty(IPropertyStore store, PropertyKey key, string value)
        {
            var variant = PropVariant.FromString(value);
            try
            {
                var result = store.SetValue(ref key, ref variant);
                if (result < 0)
                    Marshal.ThrowExceptionForHR(result);
            }
            finally
            {
                variant.Dispose();
            }
        }

        private static void SetBoolProperty(IPropertyStore store, PropertyKey key, bool value)
        {
            var variant = PropVariant.FromBool(value);
            try
            {
                var result = store.SetValue(ref key, ref variant);
                if (result < 0)
                    Marshal.ThrowExceptionForHR(result);
            }
            finally
            {
                variant.Dispose();
            }
        }

        private static string GetStringProperty(IPropertyStore store, PropertyKey key)
        {
            PropVariant variant;
            var result = store.GetValue(ref key, out variant);
            if (result < 0)
                Marshal.ThrowExceptionForHR(result);

            try
            {
                return variant.ValueType == VtLpwstr
                    ? Marshal.PtrToStringUni(variant.PointerValue)
                    : null;
            }
            finally
            {
                variant.Dispose();
            }
        }

        private static bool? GetBoolProperty(IPropertyStore store, PropertyKey key)
        {
            PropVariant variant;
            var result = store.GetValue(ref key, out variant);
            if (result < 0)
                Marshal.ThrowExceptionForHR(result);

            try
            {
                return variant.ValueType == VtBool
                    ? variant.BoolValue != 0
                    : (bool?)null;
            }
            finally
            {
                variant.Dispose();
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

        [DllImport("shell32.dll")]
        private static extern int SHGetPropertyStoreForWindow(
            IntPtr windowHandle,
            ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetCurrentPackageFullName(
            ref uint packageFullNameLength,
            StringBuilder packageFullName);

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant variant);

        [ComImport]
        [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig]
            int GetCount(out uint propertyCount);

            [PreserveSig]
            int GetAt(uint propertyIndex, out PropertyKey key);

            [PreserveSig]
            int GetValue(ref PropertyKey key, out PropVariant value);

            [PreserveSig]
            int SetValue(ref PropertyKey key, ref PropVariant value);

            [PreserveSig]
            int Commit();
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PropertyKey
        {
            internal PropertyKey(Guid formatId, uint propertyId)
            {
                FormatId = formatId;
                PropertyId = propertyId;
            }

            internal Guid FormatId;
            internal uint PropertyId;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct PropVariant : IDisposable
        {
            [FieldOffset(0)]
            private ushort valueType;

            [FieldOffset(8)]
            private IntPtr pointerValue;

            [FieldOffset(8)]
            private short boolValue;

            internal ushort ValueType => valueType;
            internal IntPtr PointerValue => pointerValue;
            internal short BoolValue => boolValue;

            internal static PropVariant FromString(string value)
            {
                return new PropVariant
                {
                    valueType = VtLpwstr,
                    pointerValue = Marshal.StringToCoTaskMemUni(value ?? "")
                };
            }

            internal static PropVariant FromBool(bool value)
            {
                return new PropVariant
                {
                    valueType = VtBool,
                    boolValue = value ? (short)-1 : (short)0
                };
            }

            public void Dispose()
            {
                PropVariantClear(ref this);
            }
        }
    }
}
