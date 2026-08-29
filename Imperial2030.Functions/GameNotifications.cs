using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        /// <summary>
        /// Admin recipient, read from configuration like every other deployment-specific value here.
        /// It was previously a compiled-in personal address, which cannot differ per environment and
        /// does not belong in source alongside SMTP settings that are already environment variables.
        /// </summary>
        private static string AdminEmail =>
            Environment.GetEnvironmentVariable("ADMIN_EMAIL") ?? "";
        private readonly ILogger _logger;

        public GameNotifications(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<GameNotifications>();
        }

        /// <summary>
        /// HTML-encodes a value before it is interpolated into an email BODY.
        ///
        /// Game names are fully user-controlled (CreateGameRequest caps them at 50 characters with no
        /// charset restriction) and player names are user-chosen too, while every body here is built by
        /// string interpolation and sent with IsBodyHtml = true. Unencoded, a game named
        /// "&lt;a href=...&gt;click here&lt;/a&gt;" injects working markup into a mail that genuinely came from
        /// this server. Only the data is encoded; the surrounding markup is developer-authored.
        ///
        /// Deliberately NOT applied to the Subject line: a subject is plain text, so encoding it would
        /// display "&amp;lt;" to the reader instead of the character they typed.
        /// </summary>
        private static string Esc(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

        private static string EscJoin(IEnumerable<string>? values) =>
            string.Join(", ", (values ?? []).Select(Esc));

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
                var data = await req.ReadFromJsonAsync<GameStartPayload>();
                if (data == null)
                {
                    _logger.LogWarning("GameStarted received empty or invalid payload.");
                    return req.CreateResponse(HttpStatusCode.BadRequest);
                }

                var subject = $"Imperial 2030: Game '{data.GameName}' has started!";
                string htmlContent = $"<h1>Game Started: {Esc(data.GameName)}</h1>" +
                                     $"<p><strong>Started:</strong> {data.StartedDate:yyyy-MM-dd HH:mm:ss}</p>" +
                                     $"<p><strong>Host:</strong> {Esc(data.HostName)}</p>" +
                                     $"<p><strong>Players:</strong> {data.PlayerCount}</p>" +
                                     $"<p><strong>Names:</strong> {EscJoin(data.PlayerNames)}</p>" +
                                     $"<p><strong>Special Rules:</strong></p>" +
                                     $"<ul>" +
                                     $"<li>Variant Bonus Only For Tax Increases: {(data.VariantBonusOnlyForTaxIncreases ? "Checked" : "Not Checked")}</li>" +
                                     $"</ul>";

                // Send to Admin. Skipped when ADMIN_EMAIL is unset rather than attempted with an empty
                // recipient, which MailMessage rejects with an ArgumentException the caller would see
                // as a 500 for an entirely optional notification.
                if (!string.IsNullOrWhiteSpace(AdminEmail))
                {
                    await SendEmailAsync(AdminEmail, subject, htmlContent);
                }
                else
                {
                    _logger.LogWarning("ADMIN_EMAIL is not configured; skipping the admin notification.");
                }

                return req.CreateResponse(HttpStatusCode.OK);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in GameStarted");
                // Logged above; the caller gets nothing but the status. ex.ToString() here returned a
                // full stack trace with file paths to whoever called the function.
                return req.CreateResponse(HttpStatusCode.InternalServerError);
            }
        }

        [Function("GameFinished")]
        public async Task<HttpResponseData> GameFinished([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
        {
            _logger.LogInformation("GameFinished trigger function processed a request.");
            try
            {
                var data = await req.ReadFromJsonAsync<GameFinishPayload>();
                if (data == null)
                {
                    _logger.LogWarning("GameFinished received empty or invalid payload.");
                    return req.CreateResponse(HttpStatusCode.BadRequest);
                }

                var subject = $"Imperial 2030: Game '{data.GameName}' has finished!";
                string sharedHtmlContent = $"<p><strong>Winner:</strong> {Esc(data.WinnerName)} ({data.WinnerVP} VP)</p>" +
                                           $"<p><strong>Started:</strong> {data.StartedDate:yyyy-MM-dd HH:mm:ss}</p>" +
                                           $"<p><strong>Host:</strong> {Esc(data.HostName)}</p>" +
                                           $"<p><strong>Players:</strong> {data.PlayerCount}</p>" +
                                           $"<p><strong>Names:</strong> {EscJoin(data.PlayerNames)}</p>" +
                                           $"<p><strong>Special Rules:</strong></p>" +
                                           $"<ul>" +
                                           $"<li>Variant Bonus Only For Tax Increases: {(data.VariantBonusOnlyForTaxIncreases ? "Checked" : "Not Checked")}</li>" +
                                           $"</ul>" +
                                           $"<p><strong>Turns Taken:</strong> {data.TurnsTaken}</p>" +
                                           $"<p><strong>Finished:</strong> {data.FinishedDate:yyyy-MM-dd HH:mm:ss}</p>" +
                                           $"<p><strong>Summary:</strong> {Esc(data.Summary)}</p>";

                string baseHtmlContent = $"<h1>Game Finished: {Esc(data.GameName)}</h1>{sharedHtmlContent}";

                string winnerHtmlContent = $"<h1>Game Finished: {Esc(data.GameName)}</h1>" +
                                           $"<h2>Congratulations to the Winner: {Esc(data.WinnerName)}!</h2>" +
                                           sharedHtmlContent;

                var sentEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Send to Players first so they get role-specific templates (like winner congratulation)
                if (data.PlayerEmails != null)
                {
                    foreach (var email in data.PlayerEmails)
                    {
                        if (!string.IsNullOrEmpty(email) && !sentEmails.Contains(email))
                        {
                            if (string.Equals(email, data.WinnerEmail, StringComparison.OrdinalIgnoreCase))
                            {
                                await SendEmailAsync(email, subject, winnerHtmlContent);
                            }
                            else
                            {
                                await SendEmailAsync(email, subject, baseHtmlContent);
                            }
                            sentEmails.Add(email);
                        }
                    }
                }

                // Send to Admin if they weren't in the game. See the GameStarted note on the empty-address
                // guard; without it an unset ADMIN_EMAIL would fail the whole finished-game notification
                // after the players had already been mailed.
                if (!string.IsNullOrWhiteSpace(AdminEmail) && !sentEmails.Contains(AdminEmail))
                {
                    await SendEmailAsync(AdminEmail, subject, baseHtmlContent);
                    sentEmails.Add(AdminEmail);
                }

                return req.CreateResponse(HttpStatusCode.OK);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in GameFinished");
                // Logged above; the caller gets nothing but the status. ex.ToString() here returned a
                // full stack trace with file paths to whoever called the function.
                return req.CreateResponse(HttpStatusCode.InternalServerError);
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
