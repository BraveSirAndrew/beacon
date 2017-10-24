using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace BeaconLib
{
    /// <summary>
    /// Counterpart of the beacon, searches for beacons
    /// </summary>
    /// <remarks>
    /// The beacon list event will not be raised on your main thread!
    /// </remarks>
    public class Probe : IDisposable
    {
        /// <summary>
        /// Remove beacons older than this
        /// </summary>
        private static readonly TimeSpan BeaconTimeout = new TimeSpan(0, 0, 0, 5); // seconds

        public event Action<IEnumerable<BeaconLocation>> BeaconsUpdated;

        private readonly Thread thread;
        private readonly EventWaitHandle waitHandle = new EventWaitHandle(false, EventResetMode.AutoReset);
		private readonly List<UdpClient> _clients = new List<UdpClient>(); 
        private IEnumerable<BeaconLocation> currentBeacons = Enumerable.Empty<BeaconLocation>();

        private bool running = true;

        public Probe(string beaconType)
        {
	        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
	        {
		        if(networkInterface.OperationalStatus != OperationalStatus.Up || networkInterface.SupportsMulticast == false)
					continue;

		        var loopbackInterfaceId = networkInterface.GetIPProperties().GetIPv4Properties().Index;
		        if (loopbackInterfaceId == NetworkInterface.LoopbackInterfaceIndex)
			        continue;


		        foreach (var address in networkInterface.GetIPProperties().UnicastAddresses)
		        {
			        if (address.Address.AddressFamily != AddressFamily.InterNetwork)
				        continue;

					var udpClient = new UdpClient();
			        udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
			        udpClient.Client.Bind(new IPEndPoint(address.Address, 0));

			        try
			        {
				        udpClient.AllowNatTraversal(true);
			        }
			        catch (Exception ex)
			        {
				        Debug.WriteLine($"Error switching on NAT traversal for interface {networkInterface.Name}: {ex.Message}");
			        }

			        udpClient.BeginReceive(ResponseReceived, udpClient);
			        _clients.Add(udpClient);
				}
			}

            BeaconType = beaconType;
            thread = new Thread(BackgroundLoop) { IsBackground = true };
        }

        public void Start()
        {
            thread.Start();
        }

        private void ResponseReceived(IAsyncResult ar)
        {
	        if (ar.AsyncState == null)
		        throw new InvalidOperationException("The ResponseReceived callback should have contained a UdpClient in AsyncState but it was null.");

	        var udp = (UdpClient)ar.AsyncState;

            var remote = new IPEndPoint(IPAddress.Any, 0);
            var bytes = udp.EndReceive(ar, ref remote);

            var typeBytes = Beacon.Encode(BeaconType).ToList();
            Debug.WriteLine(string.Join(", ", typeBytes.Select(_ => (char)_)));
            if (Beacon.HasPrefix(bytes, typeBytes))
            {
                try
                {
                    var portBytes = bytes.Skip(typeBytes.Count).Take(2).ToArray();
                    var port      = (ushort)IPAddress.NetworkToHostOrder((short)BitConverter.ToUInt16(portBytes, 0));
                    var payload   = Beacon.Decode(bytes.Skip(typeBytes.Count + 2));
                    NewBeacon(new BeaconLocation(new IPEndPoint(remote.Address, port), payload, DateTime.Now));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                }
            }

            udp.BeginReceive(ResponseReceived, udp);
        }

        public string BeaconType { get; private set; }

        private void BackgroundLoop()
        {
            while (running)
            {
                try
                {
                    BroadcastProbe();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                }

                waitHandle.WaitOne(2000);
                PruneBeacons();
            }
        }

        private void BroadcastProbe()
        {
            var probe = Beacon.Encode(BeaconType).ToArray();

	        foreach (var udp in _clients)
	        {
		        udp.Send(probe, probe.Length, new IPEndPoint(IPAddress.Broadcast, Beacon.DiscoveryPort));
			}
		}

        private void PruneBeacons()
        {
            var cutOff = DateTime.Now - BeaconTimeout;
            var oldBeacons = currentBeacons.ToList();
            var newBeacons = oldBeacons.Where(_ => _.LastAdvertised >= cutOff).ToList();
            if (oldBeacons.SequenceEqual(newBeacons)) return;

            var u = BeaconsUpdated;
	        u?.Invoke(newBeacons);
	        currentBeacons = newBeacons;
        }

        private void NewBeacon(BeaconLocation newBeacon)
        {
            var newBeacons = currentBeacons
                .Where(_ => !_.Equals(newBeacon))
                .Concat(new [] { newBeacon })
                .OrderBy(_ => _.Data)
                .ThenBy(_ => _.Address, IPEndPointComparer.Instance)
                .ToList();
            var u = BeaconsUpdated;
	        u?.Invoke(newBeacons);
	        currentBeacons = newBeacons;
        }

	    public void Stop()
        {
            running = false;
            waitHandle.Set();
            thread.Join();
        }

        public void Dispose()
        {
            try
            {
                Stop();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }
    }
}
