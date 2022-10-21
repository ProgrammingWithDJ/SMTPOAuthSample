/*
 * By David Barrett, Microsoft Ltd. 2021. Use at your own risk.  No warranties are given.
 * 
 * DISCLAIMER:
 * THIS CODE IS SAMPLE CODE. THESE SAMPLES ARE PROVIDED "AS IS" WITHOUT WARRANTY OF ANY KIND.
 * MICROSOFT FURTHER DISCLAIMS ALL IMPLIED WARRANTIES INCLUDING WITHOUT LIMITATION ANY IMPLIED WARRANTIES OF MERCHANTABILITY OR OF FITNESS FOR
 * A PARTICULAR PURPOSE. THE ENTIRE RISK ARISING OUT OF THE USE OR PERFORMANCE OF THE SAMPLES REMAINS WITH YOU. IN NO EVENT SHALL
 * MICROSOFT OR ITS SUPPLIERS BE LIABLE FOR ANY DAMAGES WHATSOEVER (INCLUDING, WITHOUT LIMITATION, DAMAGES FOR LOSS OF BUSINESS PROFITS,
 * BUSINESS INTERRUPTION, LOSS OF BUSINESS INFORMATION, OR OTHER PECUNIARY LOSS) ARISING OUT OF THE USE OF OR INABILITY TO USE THE
 * SAMPLES, EVEN IF MICROSOFT HAS BEEN ADVISED OF THE POSSIBILITY OF SUCH DAMAGES. BECAUSE SOME STATES DO NOT ALLOW THE EXCLUSION OR LIMITATION
 * OF LIABILITY FOR CONSEQUENTIAL OR INCIDENTAL DAMAGES, THE ABOVE LIMITATION MAY NOT APPLY TO YOU.
 * */

using System;
using Microsoft.Identity.Client;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Text;
using System.Net.Security;
using System.ComponentModel.DataAnnotations;

namespace SMTPOAuthSample
{
    class Program
    {
        private static TcpClient _smtpClient = null;
        private static SslStream _sslStream = null;

        static void Main(string[] args)
        {
             Console.WriteLine("Please enter the tenantID");
            var tId=Console.ReadLine();

            Console.WriteLine("-----------------------------------------------");

            Console.WriteLine("Please enter the ClientID or ApplicationID");
            var CId = Console.ReadLine();

            string emlFile = "";
            var task = TestSMTP(CId, tId, emlFile);
            task.Wait();
        }

        static async Task TestSMTP(string ClientId, string TenantId, string EmlFile = "")
        {

            // Configure the MSAL client to get tokens
            var pcaOptions = new PublicClientApplicationOptions
            {
                ClientId = ClientId,
                TenantId = TenantId

                
            };

            Console.WriteLine("Building application");
            var pca = PublicClientApplicationBuilder
                .CreateWithApplicationOptions(pcaOptions)
                
                .WithRedirectUri("http://localhost")
                .Build();

            var smtpScope = new string[] { "https://outlook.office.com/SMTP.Send" };

            try
            {
                // Make the interactive token request
                Console.WriteLine("Requesting access token (user must log-in via browser)");
                var authResult = await pca.AcquireTokenInteractive(smtpScope).ExecuteAsync();
                if (String.IsNullOrEmpty(authResult.AccessToken))
                {
                    Console.WriteLine("No token received");
                    return;
                }
                Console.WriteLine($"Token received for {authResult.Account.Username}");

                // Use the token to connect to SMTP service
                SendMessageToSelf(authResult, EmlFile);
            }
            catch (MsalException ex)
            {
                Console.WriteLine($"Error acquiring access token: {ex}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex}");
            }
            Console.WriteLine("Finished");

        }

        static string ReadSSLStream()
        {
            int bytes = -1;
            byte[] buffer = new byte[4096];
            bytes = _sslStream.Read(buffer, 0, buffer.Length);
            string response = Encoding.ASCII.GetString(buffer, 0, bytes);
            Console.WriteLine(response); // We add a blank line after the response as it makes the output easier to read
            return response;
        }

        static void WriteSSLStream(string Data)
        {
            if (!Data.EndsWith(Environment.NewLine))
                Data = $"{Data}{Environment.NewLine}";
            _sslStream.Write(Encoding.ASCII.GetBytes(Data));
            _sslStream.Flush();
            Console.Write(Data);
        }

        static string ReadNetworkStream(NetworkStream Stream)
        {
            int bytes = -1;
            byte[] buffer = new byte[4096];
            bytes = Stream.Read(buffer, 0, buffer.Length);
            string response = Encoding.ASCII.GetString(buffer, 0, bytes);
            Console.WriteLine(response); // We add a blank line after the response as it makes the output easier to read
            return response;
        }

        static void WriteNetworkStream(NetworkStream Stream, string Data)
        {
            if (!Data.EndsWith(Environment.NewLine))
                Data = $"{Data}{Environment.NewLine}";
            Stream.Write(Encoding.ASCII.GetBytes(Data));
            Stream.Flush();
            Console.Write(Data);
        }

        static void SendMessageToSelf(AuthenticationResult authResult, string EmlFile = "")
        {
            try
            {
                using (_smtpClient = new TcpClient("outlook.office365.com", 587))
                {
                    NetworkStream smtpStream = _smtpClient.GetStream();
                    try
                    {
                        // We need to initiate the TLS connection                       
                        if (!ReadNetworkStream(smtpStream).StartsWith("220"))
                            throw new Exception("Unexpected welcome message");

                        WriteNetworkStream(smtpStream, "EHLO OAuthTest.app");
                        if (!ReadNetworkStream(smtpStream).StartsWith("250"))
                            throw new Exception("Failed on EHLO");

                        WriteNetworkStream(smtpStream, "STARTTLS");
                    }
                    catch (Exception ex)
                    {
                        // We've received an error or unexpected response.  We'll send a QUIT as there's nothing more we can do.
                        Console.WriteLine(ex.Message);
                        WriteNetworkStream(smtpStream, "QUIT");
                        smtpStream.Close();
                        smtpStream = null;
                    }

                    if (smtpStream != null && ReadNetworkStream(smtpStream).StartsWith("220"))
                    {
                        // Now we can initialise and communicate over an encrypted connection
                        using (_sslStream = new SslStream(smtpStream))
                        {                            
                            _sslStream.AuthenticateAsClient("outlook.office365.com");

                            try
                            {
                                // EHLO again
                                WriteSSLStream("EHLO");
                                ReadSSLStream();

                                // Initiate OAuth login
                                WriteSSLStream("AUTH XOAUTH2");
                                if (!ReadSSLStream().StartsWith("334"))
                                    throw new Exception("Failed on AUTH XOAUTH2");

                                // Send OAuth token
                                WriteSSLStream(XOauth2(authResult));
                                if (!ReadSSLStream().StartsWith("235"))
                                    throw new Exception("Log on failed");

                                // Logged in, send test message

                                // MAIL FROM
                                WriteSSLStream($"MAIL FROM:<{authResult.Account.Username}>");
                                if (!ReadSSLStream().StartsWith("250"))
                                    throw new Exception("Failed at MAIL FROM");

                                // RCPT TO
                                WriteSSLStream($"RCPT TO:<{authResult.Account.Username}>");
                                if (!ReadSSLStream().StartsWith("250"))
                                    throw new Exception("Failed at RCPT TO");

                                // DATA
                                WriteSSLStream("DATA");
                                if (!ReadSSLStream().StartsWith("354"))
                                    throw new Exception("Failed at DATA");

                                if (String.IsNullOrEmpty(EmlFile))
                                    WriteSSLStream($"Subject: OAuth SMTP Test Message{Environment.NewLine}{Environment.NewLine}This is a test.{Environment.NewLine}.{Environment.NewLine}");
                                else
                                {
                                    // Read the .eml file and send that
                                    WriteSSLStream($"{System.IO.File.ReadAllText(EmlFile)}{Environment.NewLine}.{Environment.NewLine}");
                                }

                                ReadSSLStream();
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine(ex.Message);
                            }
                            WriteSSLStream("QUIT");
                            ReadSSLStream();

                            Console.WriteLine("Closing connection");
                        }
                    }
                    smtpStream?.Close();
                }
            }
            catch (SocketException ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        static string XOauth2(AuthenticationResult authResult)
        {
            // Create the log-in code, which is a base 64 encoded combination of user and auth token

            char ctrlA = (char)1;
            string login = $"user={authResult.Account.Username}{ctrlA}auth=Bearer {authResult.AccessToken}{ctrlA}{ctrlA}";
            var plainTextBytes = System.Text.Encoding.UTF8.GetBytes(login);
            return Convert.ToBase64String(plainTextBytes);
        }
    }
}
