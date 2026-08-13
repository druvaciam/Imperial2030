using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;


namespace Imperial2030.Functions
{
    public class GameNotifications
    {
        private const string AdminEmail = "druvaciam@protonmail.com";
        private readonly ILogger _logger;

        public GameNotifications(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<GameNotifications>();
        }

        private async Task SendEmailAsync(string toAddress, string subject, string htmlContent)
        {
            string host = Environment.GetEnvironmentVariable("SMTP_HOST") ?? "";
            string portStr = Environment.GetEnvironmentVariable("SMTP_PORT") ?? "587";
            string username = Environment.GetEnvironmentVariable("SMTP_USERNAME") ?? "";
            string password = Environment.GetEnvironmentVariable("SMTP_PASSWORD") ?? "";
            string fromAddress = Environment.GetEnvironmentVariable("SMTP_FROM") ?? username;

            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                _logger.LogError("SMTP configuration is missing. Set SMTP_HOST, SMTP_USERNAME, and SMTP_PASSWORD.");
                return;
            }

            int port = int.TryParse(portStr, out int p) ? p : 587;

            using var client = new System.Net.Mail.SmtpClient(host, port)
            {
                Credentials = new NetworkCredential(username, password),
                EnableSsl = true
            };

            using var message = new System.Net.Mail.MailMessage(fromAddress, toAddress)
            {
                Subject = subject,
                Body = htmlContent,
                IsBodyHtml = true
            };

            try
            {
                await client.SendMailAsync(message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to send email to {toAddress}");
            }
        }

        [Function("GameStarted")]
        public async Task<HttpResponseData> GameStarted([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
        {
            _logger.LogInformation("GameStarted trigger function processed a request.");
            try
            {
                if (req.Body.CanSeek) req.Body.Position = 0;
                string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                _logger.LogInformation($"Raw GameStarted body: '{requestBody}'");
                var data = JsonSerializer.Deserialize<GameStartPayload>(requestBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (data == null)
                {
                    return req.CreateResponse(HttpStatusCode.BadRequest);
                }

                var subject = $"Imperial 2030: Game '{data.GameName}' has started!";
                string htmlContent = $"<h1>Game Started: {data.GameName}</h1>" +
                                     $"<p><strong>Started:</strong> {data.StartedDate:yyyy-MM-dd HH:mm:ss}</p>" +
                                     $"<p><strong>Host:</strong> {data.HostName}</p>" +
                                     $"<p><strong>Players:</strong> {data.PlayerCount}</p>" +
                                     $"<p><strong>Names:</strong> {string.Join(", ", data.PlayerNames ?? [])}</p>" +
                                     $"<p><strong>Special Rules:</strong></p>" +
                                     $"<ul>" +
                                     $"<li>Variant Bonus Only For Tax Increases: {(data.VariantBonusOnlyForTaxIncreases ? "Checked" : "Not Checked")}</li>" +
                                     $"</ul>";

                // Send to Admin
                await SendEmailAsync(AdminEmail, subject, htmlContent);

                // Send to Players
                if (data.PlayerEmails != null)
                {
                    foreach (var email in data.PlayerEmails)
                    {
                        if (!string.IsNullOrEmpty(email))
                        {
                            await SendEmailAsync(email, subject, htmlContent);
                        }
                    }
                }

                return req.CreateResponse(HttpStatusCode.OK);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in GameStarted");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                response.WriteString(ex.ToString());
                return response;
            }
        }

        [Function("GameFinished")]
        public async Task<HttpResponseData> GameFinished([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
        {
            _logger.LogInformation("GameFinished trigger function processed a request.");
            try
            {
                if (req.Body.CanSeek) req.Body.Position = 0;
                string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                _logger.LogInformation($"Raw GameFinished body: '{requestBody}'");
                var data = JsonSerializer.Deserialize<GameFinishPayload>(requestBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (data == null)
                {
                    return req.CreateResponse(HttpStatusCode.BadRequest);
                }

                var subject = $"Imperial 2030: Game '{data.GameName}' has finished!";
                string sharedHtmlContent = $"<p><strong>Winner:</strong> {data.WinnerName} ({data.WinnerVP} VP)</p>" +
                                           $"<p><strong>Started:</strong> {data.StartedDate:yyyy-MM-dd HH:mm:ss}</p>" +
                                           $"<p><strong>Host:</strong> {data.HostName}</p>" +
                                           $"<p><strong>Players:</strong> {data.PlayerCount}</p>" +
                                           $"<p><strong>Names:</strong> {string.Join(", ", data.PlayerNames ?? [])}</p>" +
                                           $"<p><strong>Special Rules:</strong></p>" +
                                           $"<ul>" +
                                           $"<li>Variant Bonus Only For Tax Increases: {(data.VariantBonusOnlyForTaxIncreases ? "Checked" : "Not Checked")}</li>" +
                                           $"</ul>" +
                                           $"<p><strong>Turns Taken:</strong> {data.TurnsTaken}</p>" +
                                           $"<p><strong>Finished:</strong> {data.FinishedDate:yyyy-MM-dd HH:mm:ss}</p>" +
                                           $"<p><strong>Summary:</strong> {data.Summary}</p>";

                string baseHtmlContent = $"<h1>Game Finished: {data.GameName}</h1>{sharedHtmlContent}";

                string winnerHtmlContent = $"<h1>Game Finished: {data.GameName}</h1>" +
                                           $"<h2>Congratulations to the Winner: {data.WinnerName}!</h2>" +
                                           sharedHtmlContent;

                // Send to Admin
                await SendEmailAsync(AdminEmail, subject, baseHtmlContent);

                // Send to Players
                if (data.PlayerEmails != null)
                {
                    foreach (var email in data.PlayerEmails)
                    {
                        if (!string.IsNullOrEmpty(email))
                        {
                            if (string.Equals(email, data.WinnerEmail, StringComparison.OrdinalIgnoreCase))
                            {
                                await SendEmailAsync(email, subject, winnerHtmlContent);
                            }
                            else
                            {
                                await SendEmailAsync(email, subject, baseHtmlContent);
                            }
                        }
                    }
                }

                return req.CreateResponse(HttpStatusCode.OK);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in GameFinished");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                response.WriteString(ex.ToString());
                return response;
            }
        }
    }

    public class GameStartPayload
    {
        public string GameName { get; set; } = string.Empty;
        public string HostName { get; set; } = string.Empty;
        public int PlayerCount { get; set; }
        public List<string> PlayerNames { get; set; } = new List<string>();
        public bool VariantBonusOnlyForTaxIncreases { get; set; }
        public DateTime StartedDate { get; set; }
        public List<string> PlayerEmails { get; set; } = new List<string>();
    }

    public class GameFinishPayload
    {
        public string GameName { get; set; } = string.Empty;
        public string WinnerName { get; set; } = string.Empty;
        public int WinnerVP { get; set; }
        public string? WinnerEmail { get; set; }
        public int TurnsTaken { get; set; }
        public DateTime StartedDate { get; set; }
        public DateTime FinishedDate { get; set; }
        public string HostName { get; set; } = string.Empty;
        public int PlayerCount { get; set; }
        public List<string> PlayerNames { get; set; } = new List<string>();
        public bool VariantBonusOnlyForTaxIncreases { get; set; }
        public string Summary { get; set; } = string.Empty;
        public List<string> PlayerEmails { get; set; } = new List<string>();
    }
}
