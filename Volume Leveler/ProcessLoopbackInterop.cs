using System;
using System.Runtime.InteropServices;

namespace VolumeLeveler
{
    // Hand-written interop layer for Windows' "process loopback" audio
    // capture API. No official .NET wrapper exists for this, so we declare
    // exactly what we need, matching the documented native layouts and
    // vtable orders precisely - COM interop dispatches by method position,
    // so declaration order below must match the real interfaces exactly.
    internal static class ProcessLoopbackInterop
    {
        public const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";

        public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

        public enum AUDIOCLIENT_ACTIVATION_TYPE
        {
            AUDIOCLIENT_ACTIVATION_TYPE_DEFAULT = 0,
            AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1
        }

        public enum PROCESS_LOOPBACK_MODE
        {
            PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0,
            PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE = 1
        }

        public enum AUDCLNT_SHAREMODE
        {
            AUDCLNT_SHAREMODE_SHARED = 0,
            AUDCLNT_SHAREMODE_EXCLUSIVE = 1
        }

        // Stream initialization flags we need (audioclient.h).
        public const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        public const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
        public const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;

        // Set on a captured buffer when it represents silence rather than
        // real samples - we must not try to read audio data in that case.
        public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

        public const ushort WAVE_FORMAT_PCM = 1;

        [StructLayout(LayoutKind.Sequential)]
        public struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
        {
            public uint TargetProcessId;
            public PROCESS_LOOPBACK_MODE ProcessLoopbackMode;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct AUDIOCLIENT_ACTIVATION_PARAMS
        {
            public AUDIOCLIENT_ACTIVATION_TYPE ActivationType;
            public AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROPVARIANT_BLOB
        {
            public ushort vt;
            public ushort wReserved1;
            public ushort wReserved2;
            public ushort wReserved3;
            public uint cbSize;
            public IntPtr pBlobData;
        }

        private const ushort VT_BLOB = 65;

        [StructLayout(LayoutKind.Sequential)]
        public struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [ComImport]
        [Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IActivateAudioInterfaceCompletionHandler
        {
            void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
        }

        [ComImport]
        [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IActivateAudioInterfaceAsyncOperation
        {
            void GetActivateResult(
                out int activateResult,
                [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
        }

        // Only the methods we actually use are given precise signatures;
        // the others are declared in the correct vtable position so the
        // interop layer lines up correctly, even though we never call them.
        [ComImport]
        [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioClient
        {
            [PreserveSig]
            int Initialize(
                AUDCLNT_SHAREMODE shareMode,
                uint streamFlags,
                long hnsBufferDuration,
                long hnsPeriodicity,
                [In] ref WAVEFORMATEX format,
                [In] ref Guid audioSessionGuid);

            [PreserveSig] int GetBufferSize(out uint numBufferFrames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
            [PreserveSig] int IsFormatSupported(AUDCLNT_SHAREMODE shareMode, [In] ref WAVEFORMATEX format, out IntPtr closestMatch);
            [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
            [PreserveSig] int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr eventHandle);
            [PreserveSig] int GetService([In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport]
        [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioCaptureClient
        {
            [PreserveSig]
            int GetBuffer(
                out IntPtr data,
                out uint numFramesToRead,
                out uint flags,
                out ulong devicePosition,
                out ulong qpcPosition);

            [PreserveSig] int ReleaseBuffer(uint numFramesRead);
            [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
        }

        [DllImport("Mmdevapi.dll", PreserveSig = false)]
        public static extern void ActivateAudioInterfaceAsync(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            [In] ref Guid riid,
            [In] ref PROPVARIANT_BLOB activationParams,
            IActivateAudioInterfaceCompletionHandler completionHandler,
            out IActivateAudioInterfaceAsyncOperation activateOperation);

        public static PROPVARIANT_BLOB WrapActivationParams(
            AUDIOCLIENT_ACTIVATION_PARAMS activationParams, out IntPtr unmanagedBuffer)
        {
            int size = Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>();
            unmanagedBuffer = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(activationParams, unmanagedBuffer, false);

            return new PROPVARIANT_BLOB
            {
                vt = VT_BLOB,
                cbSize = (uint)size,
                pBlobData = unmanagedBuffer
            };
        }

        // The hardcoded "safe" capture format for process loopback, as
        // documented by Microsoft: CD-quality 16-bit PCM, since querying
        // the real mix format is not supported on this virtual device.
        public static WAVEFORMATEX BuildHardcodedFormat()
        {
            const ushort channels = 2;
            const uint samplesPerSec = 44100;
            const ushort bitsPerSample = 16;
            ushort blockAlign = (ushort)(channels * bitsPerSample / 8);

            return new WAVEFORMATEX
            {
                wFormatTag = WAVE_FORMAT_PCM,
                nChannels = channels,
                nSamplesPerSec = samplesPerSec,
                nAvgBytesPerSec = samplesPerSec * blockAlign,
                nBlockAlign = blockAlign,
                wBitsPerSample = bitsPerSample,
                cbSize = 0
            };
        }
    }
}