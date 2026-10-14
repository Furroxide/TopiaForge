using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using TopiaForge.Multiplayer.Network;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.NatTraversal.PortMapping
{
    /// <summary>A gateway found on the LAN: the WAN connection service's control URL, its type, and this machine's address toward it.</summary>
    internal sealed class UpnpGateway
    {
        internal UpnpGateway(Uri controlUrl, string serviceType, IPAddress localAddress)
        {
            ControlUrl = controlUrl;
            ServiceType = serviceType;
            LocalAddress = localAddress;
        }

        internal Uri ControlUrl { get; }

        internal string ServiceType { get; }

        internal IPAddress LocalAddress { get; }

        public override string ToString() => "UPnP gateway";
    }

    /// <summary>
    /// UPnP Internet Gateway Device control, best effort: an SSDP search, the device description, and the WAN connection
    /// service's GetExternalIPAddress, AddPortMapping and DeletePortMapping actions. Only a gateway on the LAN counts: an
    /// SSDP answer must come from a LAN address, its LOCATION must name that same address over plain HTTP, and the control
    /// URL must stay on it. The description is read with DTDs refused and no external resolution. Mappings carry a fixed
    /// description and never anything a player typed.
    /// </summary>
    internal sealed class UpnpIgdClient
    {
        internal const int SearchMilliseconds = 2500;
        internal const int MaximumSearchAnswerBytes = 2048;
        internal const string MappingDescription = "TopiaForge";

        internal static readonly IPEndPoint MulticastTarget = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);

        private static readonly string[] ServiceTypes =
        {
            "urn:schemas-upnp-org:service:WANIPConnection:2",
            "urn:schemas-upnp-org:service:WANIPConnection:1",
            "urn:schemas-upnp-org:service:WANPPPConnection:1"
        };

        private readonly IPAddress bindAddress;
        private readonly IPEndPoint searchTarget;
        private readonly int searchMilliseconds;

        /// <param name="bindAddress">The local address for the search socket; tests pass loopback so no firewall prompt appears.</param>
        /// <param name="searchTarget">Where the search goes: the SSDP multicast group, or a test responder.</param>
        internal UpnpIgdClient(IPAddress? bindAddress = null, IPEndPoint? searchTarget = null, int searchMilliseconds = SearchMilliseconds)
        {
            this.bindAddress = bindAddress ?? IPAddress.Any;
            this.searchTarget = searchTarget ?? MulticastTarget;
            this.searchMilliseconds = Math.Max(100, searchMilliseconds);
        }

        internal static string SearchRequest() =>
            "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n";

        /// <summary>Finds the first gateway that answers and describes a WAN connection service; null when none does in time.</summary>
        internal async Task<UpnpGateway?> DiscoverAsync(CancellationToken cancellation)
        {
            var locations = await Task.Run(() => Search(cancellation)).ConfigureAwait(false);
            foreach (var location in locations)
            {
                var response = await UpnpHttp.SendAsync(location, "GET", null, null, cancellation).ConfigureAwait(false);
                if (response == null || response.Status != 200) continue;
                if (TryParseDescription(response.Body, location, out var control, out var serviceType)) return new UpnpGateway(control, serviceType, response.LocalAddress);
            }

            return null;
        }

        internal async Task<IPAddress?> GetExternalAddressAsync(UpnpGateway gateway, CancellationToken cancellation)
        {
            var response = await Soap(gateway, "GetExternalIPAddress", Array.Empty<KeyValuePair<string, string>>(), cancellation).ConfigureAwait(false);
            if (response == null || response.Status != 200 || !TryReadValue(response.Body, "NewExternalIPAddress", out var text)) return null;
            return IPAddress.TryParse(text, out var address) && address.AddressFamily == AddressFamily.InterNetwork ? address : null;
        }

        /// <summary>Asks for a UDP mapping; returns 0 on success, the gateway's UPnP error code (725 for permanent leases only, 718 for a conflict), or -1.</summary>
        internal async Task<int> AddPortMappingAsync(UpnpGateway gateway, int externalPort, int internalPort, int leaseSeconds, CancellationToken cancellation)
        {
            var arguments = new[]
            {
                Argument("NewRemoteHost", string.Empty),
                Argument("NewExternalPort", externalPort.ToString(CultureInfo.InvariantCulture)),
                Argument("NewProtocol", "UDP"),
                Argument("NewInternalPort", internalPort.ToString(CultureInfo.InvariantCulture)),
                Argument("NewInternalClient", NatAddresses.Format(gateway.LocalAddress)),
                Argument("NewEnabled", "1"),
                Argument("NewPortMappingDescription", MappingDescription),
                Argument("NewLeaseDuration", leaseSeconds.ToString(CultureInfo.InvariantCulture))
            };
            var response = await Soap(gateway, "AddPortMapping", arguments, cancellation).ConfigureAwait(false);
            if (response == null) return -1;
            if (response.Status == 200) return 0;
            return TryReadValue(response.Body, "errorCode", out var code) && int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : -1;
        }

        internal async Task<bool> DeletePortMappingAsync(UpnpGateway gateway, int externalPort, CancellationToken cancellation)
        {
            var arguments = new[]
            {
                Argument("NewRemoteHost", string.Empty),
                Argument("NewExternalPort", externalPort.ToString(CultureInfo.InvariantCulture)),
                Argument("NewProtocol", "UDP")
            };
            var response = await Soap(gateway, "DeletePortMapping", arguments, cancellation).ConfigureAwait(false);
            return response != null && response.Status == 200;
        }

        /// <summary>Reads an SSDP answer: a 200 from a LAN address whose LOCATION is plain HTTP on that same address.</summary>
        internal static bool TryParseSearchAnswer(byte[] datagram, int count, IPAddress sender, out Uri location)
        {
            location = null!;
            if (datagram == null || count <= 0 || count > MaximumSearchAnswerBytes || sender == null) return false;
            var plain = NatAddresses.Unmap(sender);
            if (UdpPaths.Classify(plain) != TransportPathKind.Lan) return false;
            string text;
            try
            {
                text = new UTF8Encoding(false, true).GetString(datagram, 0, count);
            }
            catch (ArgumentException)
            {
                return false;
            }

            var lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0 || !lines[0].StartsWith("HTTP/1.1 200", StringComparison.Ordinal)) return false;
            foreach (var line in lines)
            {
                var colon = line.IndexOf(':');
                if (colon <= 0 || !line.Substring(0, colon).Trim().Equals("LOCATION", StringComparison.OrdinalIgnoreCase)) continue;
                if (!Uri.TryCreate(line.Substring(colon + 1).Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "http") return false;
                if (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var host) || !NatAddresses.SameAddress(host, plain)) return false;
                location = uri;
                return true;
            }

            return false;
        }

        /// <summary>Finds a WAN connection service in a device description; its control URL must stay on the gateway's host and port.</summary>
        internal static bool TryParseDescription(byte[] xml, Uri location, out Uri controlUrl, out string serviceType)
        {
            controlUrl = null!;
            serviceType = string.Empty;
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            string? baseText = null;
            try
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = UpnpHttp.MaximumResponseBytes,
                    IgnoreComments = true,
                    IgnoreProcessingInstructions = true
                };
                using (var reader = XmlReader.Create(new MemoryStream(xml), settings))
                {
                    string? type = null;
                    string? control = null;
                    var inService = false;
                    while (reader.Read())
                    {
                        if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "service")
                        {
                            inService = true;
                            type = null;
                            control = null;
                        }
                        else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "service")
                        {
                            inService = false;
                            if (type != null && control != null && !found.ContainsKey(type)) found.Add(type, control);
                        }
                        else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "URLBase" && !inService)
                        {
                            baseText = reader.ReadElementContentAsString().Trim();
                        }
                        else if (inService && reader.NodeType == XmlNodeType.Element && reader.LocalName == "serviceType")
                        {
                            type = reader.ReadElementContentAsString().Trim();
                        }
                        else if (inService && reader.NodeType == XmlNodeType.Element && reader.LocalName == "controlURL")
                        {
                            control = reader.ReadElementContentAsString().Trim();
                        }
                    }
                }
            }
            catch (XmlException)
            {
                return false;
            }

            var baseUri = location;
            if (!string.IsNullOrEmpty(baseText) && Uri.TryCreate(baseText, UriKind.Absolute, out var declared) && SameOrigin(declared, location)) baseUri = declared;
            foreach (var preferred in ServiceTypes)
            {
                if (!found.TryGetValue(preferred, out var text) || !Uri.TryCreate(baseUri, text, out var resolved) || !SameOrigin(resolved, location)) continue;
                controlUrl = resolved;
                serviceType = preferred;
                return true;
            }

            return false;
        }

        /// <summary>Writes a SOAP envelope for one action; argument values are escaped, though all of them are this side's own.</summary>
        internal static byte[] SoapEnvelope(string serviceType, string action, IReadOnlyList<KeyValuePair<string, string>> arguments)
        {
            var builder = new StringBuilder(512);
            builder.Append("<?xml version=\"1.0\"?>");
            builder.Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">");
            builder.Append("<s:Body><u:").Append(action).Append(" xmlns:u=\"").Append(serviceType).Append("\">");
            foreach (var argument in arguments)
            {
                builder.Append('<').Append(argument.Key).Append('>').Append(Escape(argument.Value)).Append("</").Append(argument.Key).Append('>');
            }

            builder.Append("</u:").Append(action).Append("></s:Body></s:Envelope>");
            return Encoding.UTF8.GetBytes(builder.ToString());
        }

        /// <summary>Reads the text of the first element named <paramref name="name"/>, ignoring namespaces.</summary>
        internal static bool TryReadValue(byte[] xml, string name, out string value)
        {
            value = string.Empty;
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = UpnpHttp.MaximumResponseBytes };
                using (var reader = XmlReader.Create(new MemoryStream(xml), settings))
                {
                    while (reader.Read())
                    {
                        if (reader.NodeType != XmlNodeType.Element || reader.LocalName != name) continue;
                        value = reader.ReadElementContentAsString().Trim();
                        return value.Length <= 64;
                    }
                }
            }
            catch (XmlException)
            {
                return false;
            }

            return false;
        }

        private Task<UpnpHttpResponse?> Soap(UpnpGateway gateway, string action, IReadOnlyList<KeyValuePair<string, string>> arguments, CancellationToken cancellation) =>
            UpnpHttp.SendAsync(gateway.ControlUrl, "POST", gateway.ServiceType + "#" + action, SoapEnvelope(gateway.ServiceType, action, arguments), cancellation);

        /// <summary>Sends the M-SEARCH twice and collects LOCATIONs for the search window, first answer first.</summary>
        private List<Uri> Search(CancellationToken cancellation)
        {
            var locations = new List<Uri>();
            var opened = PortMappingSocket.Open(bindAddress);
            if (opened == null) return locations;
            using (var socket = opened)
            {
                try
                {
                    var request = Encoding.ASCII.GetBytes(SearchRequest());
                    socket.SendTo(request, searchTarget);
                    socket.SendTo(request, searchTarget);
                }
                catch (SocketException)
                {
                    return locations;
                }

                var buffer = new byte[MaximumSearchAnswerBytes + 1];
                var deadline = Environment.TickCount + searchMilliseconds;
                while (!cancellation.IsCancellationRequested && locations.Count < 4)
                {
                    var remaining = deadline - Environment.TickCount;
                    if (remaining <= 0) break;
                    try
                    {
                        if (!socket.Poll(Math.Min(remaining, 50) * 1000, SelectMode.SelectRead)) continue;
                        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                        var received = socket.ReceiveFrom(buffer, ref from);
                        if (TryParseSearchAnswer(buffer, received, ((IPEndPoint)from).Address, out var location) && !locations.Contains(location)) locations.Add(location);
                    }
                    catch (SocketException)
                    {
                        // A reset or an oversized answer loses that answer only.
                    }
                }
            }

            return locations;
        }

        private static bool SameOrigin(Uri candidate, Uri location) =>
            candidate.Scheme == "http" && string.Equals(candidate.Host, location.Host, StringComparison.OrdinalIgnoreCase) && candidate.Port == location.Port;

        private static KeyValuePair<string, string> Argument(string name, string value) => new KeyValuePair<string, string>(name, value);

        private static string Escape(string value) =>
            value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
