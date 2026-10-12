using System;
using System.Runtime.InteropServices;

// Reads only session counters for an explicitly owned test PID. No audio is
// recorded, and no endpoint or application volume is changed.
public static class OwnedAudioMeter
{
    public sealed class Sample
    {
        public bool Found, Muted;
        public float Peak, SessionVolume;
        public int Sessions;
        public string Error;
    }

    public static Sample Read(int expectedPid)
    {
        var sample = new Sample();
        if (expectedPid <= 0) { sample.Error = "Invalid test process id"; return sample; }
        object enumeratorObject = null;
        IMMDeviceCollection devices = null;
        try
        {
            enumeratorObject = new MMDeviceEnumerator();
            var enumerator = (IMMDeviceEnumerator)enumeratorObject;
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(0, 1, out devices));
            uint deviceCount;
            Marshal.ThrowExceptionForHR(devices.GetCount(out deviceCount));
            for (uint i = 0; i < deviceCount; i++)
            {
                IMMDevice device = null;
                object managerObject = null;
                IAudioSessionEnumerator sessions = null;
                try
                {
                    Marshal.ThrowExceptionForHR(devices.Item(i, out device));
                    Guid managerId = typeof(IAudioSessionManager2).GUID;
                    Marshal.ThrowExceptionForHR(device.Activate(ref managerId, 23, IntPtr.Zero, out managerObject));
                    var manager = (IAudioSessionManager2)managerObject;
                    Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out sessions));
                    int count;
                    Marshal.ThrowExceptionForHR(sessions.GetCount(out count));
                    for (int index = 0; index < count; index++)
                    {
                        IAudioSessionControl session = null;
                        try
                        {
                            Marshal.ThrowExceptionForHR(sessions.GetSession(index, out session));
                            uint processId;
                            var identity = (IAudioSessionControl2)session;
                            Marshal.ThrowExceptionForHR(identity.GetProcessId(out processId));
                            if (processId != (uint)expectedPid) continue;
                            float peak, volume; bool muted;
                            Marshal.ThrowExceptionForHR(((IAudioMeterInformation)session).GetPeakValue(out peak));
                            var mixer = (ISimpleAudioVolume)session;
                            Marshal.ThrowExceptionForHR(mixer.GetMasterVolume(out volume));
                            Marshal.ThrowExceptionForHR(mixer.GetMute(out muted));
                            sample.Found = true;
                            sample.Sessions++;
                            sample.Peak = Math.Max(sample.Peak, peak);
                            sample.SessionVolume = Math.Max(sample.SessionVolume, volume);
                            sample.Muted = sample.Sessions == 1 ? muted : sample.Muted && muted;
                        }
                        finally { Release(session); }
                    }
                }
                finally { Release(sessions); Release(managerObject); Release(device); }
            }
        }
        catch (Exception error) { sample.Error = error.GetType().Name + ": " + error.Message; }
        finally { Release(devices); Release(enumeratorObject); }
        return sample;
    }

    private static void Release(object instance)
    {
        if (instance != null && Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance);
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint stateMask, out IMMDeviceCollection devices);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(ref Guid session, uint flags, out IAudioSessionControl control);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid session, uint flags, out ISimpleAudioVolume volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, ref Guid context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int GetSessionIdentifier(out IntPtr identifier);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr identifier);
        [PreserveSig] int GetProcessId(out uint processId);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float value, ref Guid context);
        [PreserveSig] int GetMasterVolume(out float value);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool value, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool value);
    }
}
