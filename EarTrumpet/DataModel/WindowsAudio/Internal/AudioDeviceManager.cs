using EarTrumpet.DataModel.Audio;
using EarTrumpet.Diagnosis;
using EarTrumpet.Extensions;
using EarTrumpet.Interop;
using EarTrumpet.Interop.MMDeviceAPI;
using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace EarTrumpet.DataModel.WindowsAudio.Internal
{
    class AudioDeviceManager : IMMNotificationClient, IAudioDeviceManager, IAudioDeviceManagerWindowsAudio
    {
        public event EventHandler<IAudioDevice> DefaultChanged;
        public event EventHandler Loaded;

        public ObservableCollection<IAudioDevice> Devices { get; }
        public string Kind => _kind.ToString();

        public IAudioDevice Default
        {
            get => _default;
            set
            {
                if (value != _default)
                {
                    SetDefaultDevice(value, ERole.eMultimedia);
                    DefaultChanged?.Invoke(this, Default);
                }
            }
        }


        private EDataFlow Flow => _kind == AudioDeviceKind.Playback ? EDataFlow.eRender : EDataFlow.eCapture;

        private static AutoPolicyConfigClientWin7 s_PolicyConfigClient = null;

        private IMMDeviceEnumerator _enumerator;
        private IAudioDevice _default;
        private readonly Dispatcher _dispatcher;
        private readonly AudioDeviceKind _kind;
        private readonly AudioPolicyConfig _policyConfigService;
        private readonly ConcurrentDictionary<string, IAudioDevice> _deviceMap = new ConcurrentDictionary<string, IAudioDevice>();
        private readonly FilteredCollectionChain<IAudioDevice> _deviceFilter;
        private readonly ObservableCollection<IAudioDevice> _devices = new ObservableCollection<IAudioDevice>();

        public AudioDeviceManager(AudioDeviceKind kind)
        {
            _kind = kind;
            _dispatcher = Dispatcher.CurrentDispatcher;
            _policyConfigService = new AudioPolicyConfig(Flow);
            _deviceFilter = new FilteredCollectionChain<IAudioDevice>(_devices, _dispatcher);
            Devices = _deviceFilter.Items;

            TraceLine($"Create");

            Task.Factory.StartNew(() =>
            {
                try
                {
                    _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                    _enumerator.RegisterEndpointNotificationCallback(this);

                    var devices = _enumerator.EnumAudioEndpoints(Flow, DeviceState.ACTIVE);
                    uint deviceCount = devices.GetCount();
                    for (uint i = 0; i < deviceCount; i++)
                    {
                        ((IMMNotificationClient)this).OnDeviceAdded(devices.Item(i).GetId());
                    }

                    _dispatcher.Invoke((Action)(() =>
                    {
                        QueryDefaultDevice();
                        Loaded?.Invoke(this, null);
                    }));
                }
                catch (Exception ex)
                {
                    // Even through we're going to be broken, show the tray icon so the user can collect debug data.
                    ErrorReporter.LogWarning(ex);

                    _dispatcher.Invoke((Action)(() =>
                    {
                        Loaded?.Invoke(this, null);
                    }));
                }
            });

            TraceLine($"Create Exit");
        }

        ~AudioDeviceManager()
        {
            try
            {
                _enumerator.UnregisterEndpointNotificationCallback(this);
            }
            catch (Exception ex)
            {
                TraceLine($"{ex}");
            }
        }

        private void QueryDefaultDevice()
        {
            TraceLine("QueryDefaultDevice");
            var previousDefault = _default;
            _default = GetDefaultDevice();

            // Compare instances, not ids: a recreated device keeps its id but replaces the old object.
            if (previousDefault != _default)
            {
                DefaultChanged?.Invoke(this, _default);
            }
        }

        public void SetDefaultDevice(IAudioDevice device, ERole role = ERole.eMultimedia)
        {
            TraceLine($"SetDefaultDevice {device.Id}");

            if (s_PolicyConfigClient == null)
            {
                s_PolicyConfigClient = new AutoPolicyConfigClientWin7();
            }

            // Racing with the system, the device may not be valid anymore.
            try
            {
                s_PolicyConfigClient.SetDefaultEndpoint(device.Id, role);
            }
            catch (Exception ex)
            {
                TraceLine($"{ex}");
            }
        }

        public IAudioDevice GetDefaultDevice(ERole eRole = ERole.eMultimedia)
        {
            try
            {
                var rawDevice = _enumerator.GetDefaultAudioEndpoint(Flow, ERole.eMultimedia);
                TryFind(rawDevice.GetId(), out var device);
                return device;
            }
            catch (Exception ex) when (ex.Is(HRESULT.ERROR_NOT_FOUND))
            {
                // Expected.
                return null;
            }
        }

        public void MoveHiddenAppsToDevice(string appId, string id)
        {
            foreach (var device in _devices)
            {
                ((IAudioDeviceInternal)device).MoveHiddenAppsToDevice(appId, id);
            }
        }

        public void UnhideSessionsForProcessId(string deviceId, int processId)
        {
            if (TryFind(deviceId, out var device))
            {
                ((IAudioDeviceInternal)device).UnhideSessionsForProcessId(processId);
            }
        }

        public void RefreshSessions()
        {
            var devices = _devices.ToArray();
            Task.Factory.StartNew(() =>
            {
                foreach (var device in devices)
                {
                    ((IAudioDeviceInternal)device).RefreshSessions();
                }
            });
        }

        public void UpdatePeakValues()
        {
            foreach (var device in _devices.ToArray())
            {
                ((IAudioDeviceInternal)device).UpdatePeakValue();
            }
        }

        void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId)
        {
            TraceLine($"OnDeviceAdded {pwstrDeviceId}");

            if (!TryFind(pwstrDeviceId, out IAudioDevice unused))
            {
                try
                {
                    var device = _enumerator.GetDevice(pwstrDeviceId);
                    if (((IMMEndpoint)device).GetDataFlow() == Flow &&
                        device.GetState() == DeviceState.ACTIVE)
                    {
                        var newDevice = new AudioDevice(this, device, _dispatcher);

                        _dispatcher.Invoke((Action)(() =>
                        {
                            // We must check again on the UI thread to avoid adding a duplicate device.
                            if (!TryFind(pwstrDeviceId, out IAudioDevice unused1))
                            {
                                Add(newDevice);

                                // The default device notification can arrive before the device is ready
                                // (common with Bluetooth), in which case it couldn't be resolved then.
                                QueryDefaultDevice();
                            }
                        }));
                    }
                }
                catch (Exception ex)
                {
                    // We catch Exception here because IMMDevice::Activate can return E_POINTER/NullReferenceException, as well as other expcetions listed here:
                    // https://docs.microsoft.com/en-us/dotnet/framework/interop/how-to-map-hresults-and-exceptions
                    TraceLine($"{ex}");
                }
            }
        }

        // Throws away our wrapper for an endpoint and builds a new one, the same way restarting EarTrumpet would.
        // Needed when Windows rebuilds the audio stack behind an endpoint (common with Bluetooth) without changing
        // its id, since the old session manager then stops reporting sessions.
        private void RecreateDevice(string pwstrDeviceId)
        {
            TraceLine($"RecreateDevice {pwstrDeviceId}");

            try
            {
                var device = _enumerator.GetDevice(pwstrDeviceId);
                if (((IMMEndpoint)device).GetDataFlow() != Flow ||
                    device.GetState() != DeviceState.ACTIVE)
                {
                    return;
                }

                var newDevice = new AudioDevice(this, device, _dispatcher);

                _dispatcher.Invoke((Action)(() =>
                {
                    if (TryFind(pwstrDeviceId, out IAudioDevice oldDevice))
                    {
                        Remove(oldDevice);
                    }

                    Add(newDevice);
                    QueryDefaultDevice();
                }));
            }
            catch (Exception ex)
            {
                TraceLine($"{ex}");
            }
        }

        void IMMNotificationClient.OnDeviceRemoved(string pwstrDeviceId)
        {
            TraceLine($"OnDeviceRemoved {pwstrDeviceId}");

            _dispatcher.Invoke((Action)(() =>
            {
                if (TryFind(pwstrDeviceId, out IAudioDevice dev))
                {
                    Remove(dev);
                }
            }));
        }

        void IMMNotificationClient.OnDefaultDeviceChanged(EDataFlow flow, ERole role, string pwstrDefaultDeviceId)
        {
            if (flow == Flow)
            {
                TraceLine($"OnDefaultDeviceChanged {pwstrDefaultDeviceId}");

                _dispatcher.Invoke((Action)(() =>
                {
                    QueryDefaultDevice();
                }));

                // Apps move their streams to the new default device shortly after this notification,
                // and Windows doesn't always tell us about the sessions that arrive that way.
                Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => _dispatcher.BeginInvoke((Action)RefreshSessions));
            }
        }

        void IMMNotificationClient.OnDeviceStateChanged(string pwstrDeviceId, DeviceState dwNewState)
        {
            TraceLine($"OnDeviceStateChanged {pwstrDeviceId} {dwNewState}");
            switch (dwNewState)
            {
                case DeviceState.ACTIVE:
                    if (TryFind(pwstrDeviceId, out _))
                    {
                        // We missed it going away, so what we hold may be stale.
                        RecreateDevice(pwstrDeviceId);
                    }
                    else
                    {
                        ((IMMNotificationClient)this).OnDeviceAdded(pwstrDeviceId);
                    }
                    break;
                case DeviceState.DISABLED:
                case DeviceState.NOTPRESENT:
                case DeviceState.UNPLUGGED:
                    ((IMMNotificationClient)this).OnDeviceRemoved(pwstrDeviceId);
                    break;
                default:
                    TraceLine($"Unknown DEVICE_STATE: {dwNewState}");
                    break;
            }
        }

        void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PROPERTYKEY key)
        {
            TraceLine($"OnPropertyValueChanged {pwstrDeviceId} {key.fmtid},{key.pid}");
            if (TryFind(pwstrDeviceId, out IAudioDevice dev))
            {
                if (PropertyKeys.PKEY_AudioEndPoint_Interface.Equals(key))
                {
                    // The hardware behind this endpoint changed (e.g. a Bluetooth headset reconnected),
                    // so the session manager and volume objects we hold no longer reflect it.
                    RecreateDevice(dev.Id);
                }
            }
        }

        public void SetDefaultEndPoint(string id, int pid)
        {
            // Note: We found it unexpected that SetDefaultEndPoint
            // would accept an id for an 'invalid' device and then 
            // no audio will be heard from any device for the given pid.
            if (id == null || TryFind(id, out _))
            {
                _policyConfigService.SetDefaultEndPoint(id, pid);
            }
        }

        public string GetDefaultEndPoint(int processId)
        {
            if (Environment.OSVersion.IsAtLeast(OSVersions.RS4))
            {
                return _policyConfigService.GetDefaultEndPoint(processId);
            }
            return null;
        }

        public void AddFilter(Func<ObservableCollection<IAudioDevice>, ObservableCollection<IAudioDevice>> filter)
        {
            _deviceFilter.AddFilter(filter);
        }

        private void Add(IAudioDevice device)
        {
            if (_deviceMap.TryAdd(device.Id, device))
            {
                _devices.Add(device);
            }
        }

        private void Remove(IAudioDevice device)
        {
            if (_deviceMap.TryRemove(device.Id, out var foundDevice))
            {
                _devices.Remove(device);
            }
        }

        private bool TryFind(string deviceId, out IAudioDevice found)
        {
            if (deviceId == null)
            {
                found = null;
                return false;
            }

            return _deviceMap.TryGetValue(deviceId, out found);
        }

        private void TraceLine(string message)
        {
            System.Diagnostics.Trace.WriteLine($"AudioDeviceManager-({_kind}) {message}");
        }
    }
}
