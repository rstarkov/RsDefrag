﻿//
// a set of simple C# wrappers over the NT Defragmenter APIs
//

//
// Refrences

//
// http://www.sysinternals.com/ntw2k/info/defrag.shtml
//
// msdn how-to
// ms-help://MS.MSDNQTR.2004JUL.1033/fileio/base/defragmenting_files.htm
// http://msdn.microsoft.com/library/default.asp?url=/library/en-us/fileio/base/defragmenting_files.asp
//
// FSCTL_GET_VOLUME_BITMAP
// http://msdn.microsoft.com/library/default.asp?url=/library/en-us/fileio/base/fsctl_get_volume_bitmap.asp
//
// interesting structures...
// FSCTL_MOVE_FILE
// FSCTL_GET_RETRIEVAL_POINTERS
// RETRIEVAL_POINTERS_BUFFER
// FSCTL_GET_RETRIEVAL_POINTERS
//
// DeviceIoControl
// http://msdn.microsoft.com/library/default.asp?url=/library/en-us/devio/base/deviceiocontrol.asp
//

using System;
using System.Diagnostics;
using System.Collections;
using System.Runtime.InteropServices;
using System.ComponentModel;

namespace RomanDefrag
{
    public struct FileMapEntry
    {
        /// <summary>Virtual cluster number: relative offset within a file</summary>
        public long Vcn;
        /// <summary>Logical cluster number: relative offset within drive</summary>
        public long Lcn;

        public override string ToString() => $"VCN = {Vcn:#,0}, LCN = {Lcn:#,0}";
    }

    public class IOWrapper
    {

        //
        // CreateFile constants
        //
        const uint FILE_SHARE_READ = 0x00000001;
        const uint FILE_SHARE_WRITE = 0x00000002;
        const uint FILE_SHARE_DELETE = 0x00000004;
        const uint OPEN_EXISTING = 3;

        const uint GENERIC_READ = (0x80000000);
        const uint GENERIC_WRITE = (0x40000000);

        const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
        const uint FILE_READ_ATTRIBUTES = (0x0080);
        const uint FILE_WRITE_ATTRIBUTES = 0x0100;
        const uint ERROR_INSUFFICIENT_BUFFER = 122;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern int CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(
            IntPtr hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            [Out] IntPtr lpOutBuffer,
            uint nOutBufferSize,
            ref uint lpBytesReturned,
            IntPtr lpOverlapped);

        static private IntPtr OpenVolume(string DeviceName)
        {
            IntPtr hDevice;
            hDevice = CreateFile(
                @"\\.\" + DeviceName,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);
            if ((int) hDevice == -1)
            {
                throw new Win32Exception();
            }
            return hDevice;
        }

        static private IntPtr OpenFile(string path)
        {
            IntPtr hFile;
            hFile = CreateFile(
                        path,
                        FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES,
                        FILE_SHARE_READ | FILE_SHARE_WRITE,
                        IntPtr.Zero,
                        OPEN_EXISTING,
                        0,
                        IntPtr.Zero);
            if ((int) hFile == -1)
            {
                throw new Win32Exception();
            }
            return hFile;
        }


        /// <summary>
        ///     Get cluster usage for a device</summary>
        /// <param name="DeviceName">
        ///     use "c:"</param>
        /// <returns>
        ///     a bitarray for each cluster</returns>
        static public BitArray GetVolumeMap(string DeviceName)
        {
            IntPtr pAlloc = IntPtr.Zero;
            IntPtr hDevice = IntPtr.Zero;

            try
            {
                hDevice = OpenVolume(DeviceName);

                long i64 = 0;

                GCHandle handle = GCHandle.Alloc(i64, GCHandleType.Pinned);
                IntPtr p = handle.AddrOfPinnedObject();

                // alloc off more than enough for my machine
                // 64 megs == 67108864 bytes == 536870912 bits == cluster count
                // NTFS 4k clusters == 2147483648 k of storage == 2097152 megs == 2048 gig disk storage
                uint q = 1024 * 1024 * 256; // 1024 bytes == 1k * 1024 == 1 meg * 64 == 64 megs

                uint size = 0;
                pAlloc = Marshal.AllocHGlobal((int) q);
                IntPtr pDest = pAlloc;

                bool fResult = DeviceIoControl(
                    hDevice,
                    FSConstants.FSCTL_GET_VOLUME_BITMAP,
                    p,
                    (uint) Marshal.SizeOf(i64),
                    pDest,
                    q,
                    ref size,
                    IntPtr.Zero);

                if (!fResult)
                {
                    throw new Win32Exception();
                }
                handle.Free();

                /*
                object returned was...
          typedef struct
          {
           LARGE_INTEGER StartingLcn;
           LARGE_INTEGER BitmapSize;
           BYTE Buffer[1];
          } VOLUME_BITMAP_BUFFER, *PVOLUME_BITMAP_BUFFER;
                */
                long StartingLcn = (long) Marshal.PtrToStructure(pDest, typeof(long));

                if (StartingLcn != 0)
                    throw new InvalidOperationException();

                pDest = (IntPtr) ((long) pDest + 8);
                long BitmapSize = (long) Marshal.PtrToStructure(pDest, typeof(long));

                int byteSize = (int) (BitmapSize / 8);
                byteSize++; // round up - even with no remainder

                IntPtr BitmapBegin = (IntPtr) ((long) pDest + 8);

                byte[] byteArr = new byte[byteSize];

                Marshal.Copy(BitmapBegin, byteArr, 0, (int) byteSize);

                BitArray retVal = new BitArray(byteArr);
                retVal.Length = (int) BitmapSize; // truncate to exact cluster count
                return retVal;
            }
            finally
            {
                CloseHandle(hDevice);
                hDevice = IntPtr.Zero;

                Marshal.FreeHGlobal(pAlloc);
                pAlloc = IntPtr.Zero;
            }
        }

        /// <summary>
        ///     returns a 2*number of extents array - the vcn and the lcn as pairs</summary>
        /// <param name="path">
        ///     file to get the map for ex: "c:\windows\explorer.exe"</param>
        /// <returns>
        ///     An array of [virtual cluster, physical cluster]</returns>
        static public FileMapEntry[] GetFileMap(string path)
        {
            IntPtr hFile = IntPtr.Zero;
            IntPtr pAlloc = IntPtr.Zero;

            try
            {
                hFile = OpenFile(path);

                long i64 = 0;

                GCHandle handle = GCHandle.Alloc(i64, GCHandleType.Pinned);
                IntPtr p = handle.AddrOfPinnedObject();

                uint q = 1024 * 1024 * 64; // 1024 bytes == 1k * 1024 == 1 meg * 64 == 64 megs

                uint size = 0;
                pAlloc = Marshal.AllocHGlobal((int) q);
                IntPtr pDest = pAlloc;
                bool fResult = DeviceIoControl(
                    hFile,
                    FSConstants.FSCTL_GET_RETRIEVAL_POINTERS,
                    p,
                    (uint) Marshal.SizeOf(i64),
                    pDest,
                    q,
                    ref size,
                    IntPtr.Zero);

                if (!fResult)
                {
                    throw new Win32Exception();
                }

                handle.Free();

                /*
                returned back one of...
                 typedef struct RETRIEVAL_POINTERS_BUFFER { 
                 DWORD ExtentCount; 
                 LARGE_INTEGER StartingVcn; 
                 struct {
                     LARGE_INTEGER NextVcn;
                  LARGE_INTEGER Lcn;
                    } Extents[1];
                 } RETRIEVAL_POINTERS_BUFFER, *PRETRIEVAL_POINTERS_BUFFER;
                */

                int ExtentCount = (int) Marshal.PtrToStructure(pDest, typeof(int));

                pDest = (IntPtr) ((long) pDest + (Environment.Is64BitOperatingSystem ? 8 : 4));

                long StartingVcn = (long) Marshal.PtrToStructure(pDest, typeof(long));

                if (StartingVcn != 0)
                    throw new InvalidOperationException();

                pDest = (IntPtr) ((long) pDest + 8);

                // now pDest points at an array of pairs of Int64s.

                var result = new FileMapEntry[ExtentCount];

                for (int i = 0; i < ExtentCount; i++)
                {
                    result[i].Vcn = (long) Marshal.PtrToStructure(pDest, typeof(long));
                    pDest = (IntPtr) ((long) pDest + 8);
                    result[i].Lcn = (long) Marshal.PtrToStructure(pDest, typeof(long));
                    pDest = (IntPtr) ((long) pDest + 8);
                }

                return result;
            }
            finally
            {
                CloseHandle(hFile);
                hFile = IntPtr.Zero;

                Marshal.FreeHGlobal(pAlloc);
                pAlloc = IntPtr.Zero;
            }
        }

        /// <summary>input structure for use in MoveFile</summary>
        private struct MoveFileData
        {
            public IntPtr hFile;
            public long StartingVCN;
            public long StartingLCN;
            public int ClusterCount;
        }

        /// <summary>
        ///     move a virtual cluster for a file to a logical cluster on disk, repeat for count clusters</summary>
        /// <param name="deviceName">
        ///     device to move on"c:"</param>
        /// <param name="path">
        ///     file to muck with "c:\windows\explorer.exe"</param>
        /// <param name="VCN">
        ///     cluster number in file to move</param>
        /// <param name="LCN">
        ///     cluster on disk to move to</param>
        /// <param name="count">
        ///     for how many clusters</param>
        static public void MoveFile(string deviceName, string path, long VCN, long LCN, int count)
        {
            IntPtr hVol = IntPtr.Zero;
            IntPtr hFile = IntPtr.Zero;
            try
            {
                hVol = OpenVolume(deviceName);

                hFile = OpenFile(path);


                MoveFileData mfd = new MoveFileData();
                mfd.hFile = hFile;
                mfd.StartingVCN = VCN;
                mfd.StartingLCN = LCN;
                mfd.ClusterCount = count;

                GCHandle handle = GCHandle.Alloc(mfd, GCHandleType.Pinned);
                IntPtr p = handle.AddrOfPinnedObject();
                uint bufSize = (uint) Marshal.SizeOf(mfd);
                uint size = 0;

                bool fResult = DeviceIoControl(
                    hVol,
                    FSConstants.FSCTL_MOVE_FILE,
                    p,
                    bufSize,
                    IntPtr.Zero, // no output data from this FSCTL
                    0,
                    ref size,
                    IntPtr.Zero);

                handle.Free();

                if (!fResult)
                {
                    throw new Win32Exception();
                }
            }
            finally
            {
                CloseHandle(hVol);
                CloseHandle(hFile);
            }
        }
    }


    /// <summary>constants lifted from winioctl.h from platform sdk</summary>
    internal class FSConstants
    {
        const uint FILE_DEVICE_FILE_SYSTEM = 0x00000009;

        const uint METHOD_NEITHER = 3;
        const uint METHOD_BUFFERED = 0;

        const uint FILE_ANY_ACCESS = 0;
        const uint FILE_SPECIAL_ACCESS = FILE_ANY_ACCESS;

        public static uint FSCTL_GET_VOLUME_BITMAP = CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 27, METHOD_NEITHER, FILE_ANY_ACCESS);
        public static uint FSCTL_GET_RETRIEVAL_POINTERS = CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 28, METHOD_NEITHER, FILE_ANY_ACCESS);
        public static uint FSCTL_MOVE_FILE = CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 29, METHOD_BUFFERED, FILE_SPECIAL_ACCESS);

        static uint CTL_CODE(uint DeviceType, uint Function, uint Method, uint Access)
        {
            return ((DeviceType) << 16) | ((Access) << 14) | ((Function) << 2) | (Method);
        }
    }

}
