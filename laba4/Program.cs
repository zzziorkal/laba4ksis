using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace HttpForwarder
{
    public class EntryPoint
    {
        static async Task Main(string[] args)
        {
            IPAddress listeningAddress = null;

            // Address selection loop
            while (true)
            {
                Console.Write("Ukazhite IP dlya proslushivaniya (Enter = 127.0.0.1): ");
                string addressInput = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(addressInput))
                {
                    listeningAddress = IPAddress.Loopback;
                    break;
                }

                if (IPAddress.TryParse(addressInput, out listeningAddress))
                    break;

                Console.WriteLine("Nevernyy format IP. Poprobuyte snova.");
            }

            int selectedPort = 0;

            // Port selection loop
            while (true)
            {
                Console.Write("Ukazhite port (Enter = 8888): ");
                string portInput = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(portInput))
                {
                    selectedPort = 8888;
                    break;
                }

                if (int.TryParse(portInput, out selectedPort) && selectedPort > 0 && selectedPort <= 65535)
                    break;

                Console.WriteLine("Nevernyy port. Dopustimo ot 1 do 65535.");
            }

            TcpListener proxyListener;
            try
            {
                proxyListener = new TcpListener(listeningAddress, selectedPort);
                proxyListener.Start();
                Console.WriteLine($"\nProksi zapushchen na {listeningAddress}:{selectedPort}. Ozhidanie vkhodyashchikh soedineniy...\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Oshibka zapuska: {ex.Message}");
                return;
            }

            // Main accept loop
            while (true)
            {
                try
                {
                    var incomingConnection = await proxyListener.AcceptTcpClientAsync();
                    _ = Task.Run(() => ProcessConnectionAsync(incomingConnection));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Oshibka pri prinyatii novogo podklyucheniya: {ex.Message}");
                }
            }
        }

        static async Task ProcessConnectionAsync(TcpClient clientSocket)
        {
            using (clientSocket)
            {
                try
                {
                    var clientDataStream = clientSocket.GetStream();

                    byte[] readBuffer = new byte[8192];
                    int receivedBytes = await clientDataStream.ReadAsync(readBuffer, 0, readBuffer.Length);

                    if (receivedBytes == 0) return;

                    string rawRequest = Encoding.ASCII.GetString(readBuffer, 0, receivedBytes);

                    int endOfFirstLine = rawRequest.IndexOf("\r\n");
                    if (endOfFirstLine == -1) return;

                    string requestFirstLine = rawRequest.Substring(0, endOfFirstLine);
                    string[] lineParts = requestFirstLine.Split(' ');

                    if (lineParts.Length < 3) return;

                    string httpMethod = lineParts[0];
                    string requestUrl = lineParts[1];
                    string httpVersion = lineParts[2];

                    if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out Uri parsedUri))
                    {
                        return;
                    }

                    string modifiedFirstLine = $"{httpMethod} {parsedUri.PathAndQuery} {httpVersion}\r\n";
                    byte[] modifiedFirstLineBytes = Encoding.ASCII.GetBytes(modifiedFirstLine);

                    using (TcpClient remoteServer = new TcpClient())
                    {
                        await remoteServer.ConnectAsync(parsedUri.Host, parsedUri.Port);

                        using (NetworkStream remoteStream = remoteServer.GetStream())
                        {
                            // Send modified request header
                            await remoteStream.WriteAsync(modifiedFirstLineBytes, 0, modifiedFirstLineBytes.Length);

                            int remainingDataLength = receivedBytes - (endOfFirstLine + 2);
                            if (remainingDataLength > 0)
                            {
                                await remoteStream.WriteAsync(readBuffer, endOfFirstLine + 2, remainingDataLength);
                            }

                            // Start bidirectional relay tasks
                            Task forwardToServer = TunnelDataAsync(clientDataStream, remoteStream);

                            byte[] responseBuffer = new byte[8192];
                            int responseBytes = await remoteStream.ReadAsync(responseBuffer, 0, responseBuffer.Length);

                            if (responseBytes > 0)
                            {
                                string responseHeader = Encoding.ASCII.GetString(responseBuffer, 0, responseBytes);
                                int headerEndPos = responseHeader.IndexOf("\r\n");

                                if (headerEndPos != -1)
                                {
                                    string statusLine = responseHeader.Substring(0, headerEndPos);
                                    string[] statusComponents = statusLine.Split(new char[] { ' ' }, 3);

                                    if (statusComponents.Length >= 2)
                                    {
                                        string statusCode = statusComponents[1];
                                        string statusDescription = statusComponents.Length == 3 ? statusComponents[2] : "";

                                        Console.WriteLine($"{requestUrl} - {statusCode} {statusDescription}");
                                    }
                                }

                                await clientDataStream.WriteAsync(responseBuffer, 0, responseBytes);
                            }

                            Task forwardToClient = TunnelDataAsync(remoteStream, clientDataStream);

                            await Task.WhenAll(forwardToServer, forwardToClient);
                        }
                    }
                }
                catch (Exception)
                {
                    // Silent error handling
                }
            }
        }

        static async Task TunnelDataAsync(NetworkStream sourceStream, NetworkStream destinationStream)
        {
            byte[] dataBuffer = new byte[8192];
            int bytesRead;
            try
            {
                while ((bytesRead = await sourceStream.ReadAsync(dataBuffer, 0, dataBuffer.Length)) > 0)
                {
                    await destinationStream.WriteAsync(dataBuffer, 0, bytesRead);
                }
            }
            catch (Exception)
            {
                // Silent error handling
            }
        }
    }
}