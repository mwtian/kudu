﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

namespace Kudu.Core.Infrastructure
{
    public enum HandleType
    {
        Unknown,
        Other,
        File,
        Directory
    }

    public class HandleInfo
    {
        /*
         * Ideally we will grab all the Network providers from HKLM\SYSTEM\CurrentControlSet\Control\NetworkProvider\Order
         * and look for their Device names under HKLM\SYSTEM\CurrentControlSet\Services\<NetworkProviderName>\NetworkProvider\DeviceName
         * http://msdn.microsoft.com/en-us/library/windows/hardware/ff550865%28v=vs.85%29.aspx
         * However, these providers are generally for devices that are not supported on Azure, so there is no value in adding them.
        */

        private const string NetworkDevicePrefix = "\\Device\\Mup\\";

        private const string NetworkPrefix = "\\\\";

        private const string SiteWwwroot = "SITE\\WWWROOT";

        private const string HomeEnvironmentVariable = "%HOME%";

        private const int MaxPath = 260;

        private readonly TimeSpan NtQueryObjectTimeout = TimeSpan.FromMilliseconds(50);

        public uint ProcessId { get; private set; }

        public ushort Handle { get; private set; }

        public int GrantedAccess { get; private set; }

        public byte RawType { get; private set; }

        private static readonly ConcurrentDictionary<byte, string> RawTypeMap = new ConcurrentDictionary<byte, string>();

        private string _dosFilePath;

        public string DosFilePath
        {
            get
            {
                if (_dosFilePath == null)
                {
                    InitDosFilePath();
                }
                return _dosFilePath;
            }
        }

        private string _name;

        public string Name
        {
            get
            {
                if (_name == null)
                {
                    InitTypeAndName();
                }
                return _name;
            }

            private set
            {
                _name = value;
            }
        }

        private string _typeString;

        public string TypeString
        {
            get
            {
                if (_typeString == null)
                {
                    InitType();
                }
                return _typeString;
            }

            private set
            {
                _typeString = value;
            }
        }

        public HandleType Type 
        { 
            get
            {
                return HandleTypeFromString(TypeString);
            }
        }

        private static string _homePath;

        public static string HomePath
        {
            get
            {
                if (_homePath == null)
                {
                    _homePath = System.Environment.ExpandEnvironmentVariables(HomeEnvironmentVariable);
                    if (_homePath == HomeEnvironmentVariable)
                        _homePath = null;
                }
                return _homePath;
            }
        }

        private static string _uncPath;

        public static string UncPath
        {
            get
            {
                if (_uncPath == null && HomePath != null)
                {
                    IntPtr wwwrootHandle = IntPtr.Zero;
                    try
                    {
                        wwwrootHandle = FileHandleNativeMethods.CreateFile(Path.Combine(HomePath, SiteWwwroot),
                            FileAccess.Read,
                            FileShare.ReadWrite,
                            IntPtr.Zero,
                            FileMode.Open,
                            FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS,
                            IntPtr.Zero);
                        var wwwrootPath = GetNameFromHandle(wwwrootHandle);
                        _uncPath = Regex.Replace(wwwrootPath, Regex.Escape("\\" + SiteWwwroot), String.Empty,
                            RegexOptions.IgnoreCase);
                        _uncPath = Regex.Replace(_uncPath, Regex.Escape(NetworkDevicePrefix), NetworkPrefix,
                            RegexOptions.IgnoreCase);
                    }
                    finally
                    {
                        if (wwwrootHandle != IntPtr.Zero)
                        {
                            FileHandleNativeMethods.CloseHandle(wwwrootHandle);
                        }
                    }
                }
                return _uncPath;
            }
        }

        private static readonly ConcurrentDictionary<string, string> _deviceMap = new ConcurrentDictionary<string, string>();

        private static ConcurrentDictionary<string, string> DeviceMap
        {
            get
            {
                if (_deviceMap.Count == 0)
                {
                    var logicalDrives = System.Environment.GetLogicalDrives();
                    var lpTargetPath = new StringBuilder(MaxPath);
                    foreach (string drive in logicalDrives)
                    {
                        string lpDeviceName = drive.Substring(0, 2);
                        FileHandleNativeMethods.QueryDosDevice(lpDeviceName, lpTargetPath, MaxPath);
                        _deviceMap.TryAdd(NormalizeDeviceName(lpTargetPath.ToString()), lpDeviceName);
                    }
                    _deviceMap.TryAdd(NetworkDevicePrefix.Substring(0, NetworkDevicePrefix.Length - 1), "\\");
                }
                return _deviceMap;
            }
        }

        public HandleInfo(uint processId, ushort handle, int grantedAccess, byte rawType)
        {
            ProcessId = processId;
            Handle = handle;
            GrantedAccess = grantedAccess;
            RawType = rawType;
        }

        private void InitDosFilePath()
        {
            if (Name != null)
            {
                int i = Name.Length;
                while (i > 0 && (i = Name.LastIndexOf('\\', i - 1)) != -1)
                {
                    string drive;
                    if (DeviceMap.TryGetValue(Name.Substring(0, i), out drive))
                    {
                        _dosFilePath = string.Concat(drive, Name.Substring(i));

                        if (UncPath != null && HomePath != null)
                        {
                            _dosFilePath = Regex.Replace(_dosFilePath, Regex.Escape(UncPath), HomePath,
                            RegexOptions.IgnoreCase);
                        }
                    }
                }
            }
        }

        private static string NormalizeDeviceName(string deviceName)
        {
            if (string.Compare(deviceName, 0, NetworkDevicePrefix, 0, NetworkDevicePrefix.Length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                string shareName = deviceName.Substring(deviceName.IndexOf('\\', NetworkDevicePrefix.Length) + 1);
                return string.Concat(NetworkDevicePrefix, shareName);
            }
            return deviceName;
        }

        private void InitType()
        {
            if (RawTypeMap.ContainsKey(RawType))
            {
                TypeString = RawTypeMap[RawType];
            }
            else
            {
                InitTypeAndName();
            }
        }

        bool _typeAndNameAttempted;

        private void InitTypeAndName()
        {
           if (_typeAndNameAttempted)
                return;
            _typeAndNameAttempted = true;
            IntPtr sourceProcessHandle = IntPtr.Zero;
            IntPtr handleDuplicate = IntPtr.Zero;

            try
            {

                sourceProcessHandle = FileHandleNativeMethods.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_DUP_HANDLE, true,
                    ProcessId);

                // To read info about a handle owned by another process we must duplicate it into ours
                // For simplicity, current process handles will also get duplicated; remember that process handles cannot be compared for equality
                if (!FileHandleNativeMethods.DuplicateHandle(sourceProcessHandle,
                    (IntPtr) Handle,
                    FileHandleNativeMethods.GetCurrentProcess(),
                    out handleDuplicate,
                    0,
                    false,
                    DUPLICATE_HANDLE_OPTIONS.DUPLICATE_SAME_ACCESS))
                {
                    return;
                }

                // Query the object type
                if (RawTypeMap.ContainsKey(RawType))
                {
                    TypeString = RawTypeMap[RawType];
                }
                else
                {
                    uint length;
                    FileHandleNativeMethods.NtQueryObject(handleDuplicate,
                        OBJECT_INFORMATION_CLASS.ObjectTypeInformation,
                        IntPtr.Zero,
                        0,
                        out length);

                    IntPtr ptr = IntPtr.Zero;
                    try
                    {
                        ptr = Marshal.AllocHGlobal((int) length);
                        if (FileHandleNativeMethods.NtQueryObject(handleDuplicate,
                            OBJECT_INFORMATION_CLASS.ObjectTypeInformation,
                            ptr,
                            length,
                            out length) != NTSTATUS.STATUS_SUCCESS)
                        {
                            return;
                        }

                        var typeInformation = (PUBLIC_OBJECT_TYPE_INFORMATION) Marshal.PtrToStructure(ptr, typeof (PUBLIC_OBJECT_TYPE_INFORMATION));
                        TypeString = typeInformation.TypeName.ToString();
                        RawTypeMap[RawType] = TypeString;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(ptr);
                    }
                }
                /*
                 * NtQueryObject can hang if called on a synchronous handle that is blocked on an operation (usually a read on pipes, but true for any synchronous handle)
                 * The process can also have handles over the network which might be slow to resolve.
                 * Therefore, I think having a timeout on the NtQueryObject is the correct approach. Timeout is currently 50 msec.
                 */
                ExecuteWithTimeout(() => { Name = GetNameFromHandle(handleDuplicate); }, NtQueryObjectTimeout);
            }
            finally
            {
                FileHandleNativeMethods.CloseHandle(sourceProcessHandle);
                if (handleDuplicate != IntPtr.Zero)
                {
                    FileHandleNativeMethods.CloseHandle(handleDuplicate);
                }
            }
        }

        private static string GetNameFromHandle(IntPtr handle)
        {
            uint length;

            FileHandleNativeMethods.NtQueryObject(
                handle,
                OBJECT_INFORMATION_CLASS.ObjectNameInformation,
                IntPtr.Zero, 0, out length);
            IntPtr ptr = IntPtr.Zero;
            try
            {
                ptr = Marshal.AllocHGlobal((int) length);
                if (FileHandleNativeMethods.NtQueryObject(
                    handle,
                    OBJECT_INFORMATION_CLASS.ObjectNameInformation,
                    ptr, length, out length) != NTSTATUS.STATUS_SUCCESS)
                {
                    return null;
                }
                var unicodeStringName = (UNICODE_STRING) Marshal.PtrToStructure(ptr, typeof (UNICODE_STRING));
                return unicodeStringName.ToString();
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        private static void ExecuteWithTimeout(Action action, TimeSpan timeout)
        {
            var cancellationToken = new CancellationTokenSource();
            var task = new Task(action, cancellationToken.Token);
            task.Start();
            if (task.Wait(timeout))
                return;
            cancellationToken.Cancel(false);
        }

        public static HandleType HandleTypeFromString(string typeStr)
        {
            switch (typeStr)
            {
                case null: 
                    return HandleType.Unknown;
                case "File": 
                    return HandleType.File;
                case "Directory": 
                    return HandleType.Directory;
                default: 
                    return HandleType.Other;
            }
        }
    }
}
