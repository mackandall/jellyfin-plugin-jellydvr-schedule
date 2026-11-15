using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DvrSchedule.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DvrSchedule.Alerts;

/// <summary>Sends an alert to a phone (ntfy) and by email.</summary>
public sealed class AlertSenders
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<AlertSenders> _logger;

    public AlertSenders(IHttpClientFactory http, ILogger<AlertSenders> logger)
    {
        _http = http;
        _logger = logger;
    }

    public enum Message
    {
        New,
        Fixed,
        Reminder,
        Test
    }

    /// <summary>Sends to the route's phone/email (those set up); returns where it went.</summary>
    public async Task<List<string>> SendAsync(Alert a, Message kind, AlertRoute route, PluginConfiguration cfg, CancellationToken ct)
    {
        var sent = new List<string>();
        var (title, body) = Compose(a, kind);
        if (route.Phone && cfg.NtfyTopic.Trim().Length > 0)
        {
            try
            {
                await SendNtfyAsync(title, body, Priority(a, kind, cfg), kind == Message.Fixed ? "white_check_mark" : "warning", cfg, ct).ConfigureAwait(false);
                sent.Add("phone");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not send the alert to ntfy: {Title}", title);
            }
        }

        if (route.Email && cfg.EmailTo.Trim().Length > 0 && cfg.SmtpHost.Trim().Length > 0)
        {
            try
            {
                await SendEmailAsync(title, body, cfg, ct).ConfigureAwait(false);
                sent.Add("email");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not send the alert by email: {Title}", title);
            }
        }

        return sent;
    }

    /// <summary>A test message with the settings given (not yet saved); throws with the reason on failure.</summary>
    public async Task TestAsync(string to, PluginConfiguration cfg, CancellationToken ct)
    {
        const string Title = "JellyDVR test alert";
        const string Body = "Alerts reach you here. You'll get a message like this when a recording fails, a tuner is stuck, or the drive fills up.";
        if (to == "email")
        {
            await SendEmailAsync(Title, Body, cfg, ct).ConfigureAwait(false);
        }
        else
        {
            await SendNtfyAsync(Title, Body, 3, "tada", cfg, ct).ConfigureAwait(false);
        }
    }

    private static (string Title, string Body) Compose(Alert a, Message kind)
    {
        var lines = new List<string>();
        switch (kind)
        {
            case Message.Fixed:
                lines.Add(a.ClosedText ?? "It cleared by itself.");
                lines.Add("Was: " + a.Text);
                break;
            case Message.Reminder:
                lines.Add(a.Text);
                lines.Add("Open since " + AlertCenter.When(a.CreatedUtc) + ".");
                break;
            default:
                lines.Add(a.Text);
                break;
        }

        if (kind != Message.Fixed && !string.IsNullOrEmpty(a.Next))
        {
            lines.Add(a.Next!);
        }

        var title = kind switch
        {
            Message.Fixed => "Fixed: " + a.Title,
            Message.Reminder => "Still open: " + a.Title,
            _ => a.Title
        };
        return (title, string.Join("\n", lines));
    }

    // ntfy: 2 = no sound, 3 = normal, 4 = high.
    private static int Priority(Alert a, Message kind, PluginConfiguration cfg)
    {
        var p = kind == Message.Fixed ? 2 : a.Kind == AlertKinds.Failed && kind == Message.New ? 4 : 3;
        if (p > 2 && InQuietHours(cfg, DateTime.Now) && !(a.Kind == AlertKinds.Failed && cfg.FailedLoudInQuiet))
        {
            p = 2;
        }

        return p;
    }

    /// <summary>Whether <paramref name="local"/> falls in the quiet hours (which may run past midnight).</summary>
    public static bool InQuietHours(PluginConfiguration cfg, DateTime local)
    {
        if (!cfg.QuietHours
            || !TimeSpan.TryParse(cfg.QuietFrom, CultureInfo.InvariantCulture, out var from)
            || !TimeSpan.TryParse(cfg.QuietTo, CultureInfo.InvariantCulture, out var to)
            || from == to)
        {
            return false;
        }

        var t = local.TimeOfDay;
        return from < to ? t >= from && t < to : t >= from || t < to;
    }

    private async Task SendNtfyAsync(string title, string body, int priority, string tag, PluginConfiguration cfg, CancellationToken ct)
    {
        var server = (string.IsNullOrWhiteSpace(cfg.NtfyServer) ? "https://ntfy.sh" : cfg.NtfyServer.Trim()).TrimEnd('/');
        var topic = cfg.NtfyTopic.Trim();
        if (topic.Length == 0)
        {
            throw new InvalidOperationException("No ntfy topic is set.");
        }

        // JSON publishing keeps titles with accents and dashes intact (headers would need encoding).
        using var req = new HttpRequestMessage(HttpMethod.Post, server + "/")
        {
            Content = JsonContent.Create(new { topic, title, message = body, priority, tags = new[] { tag } })
        };
        if (!string.IsNullOrWhiteSpace(cfg.NtfyToken))
        {
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cfg.NtfyToken.Trim());
        }

        using var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        using var res = await client.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            var why = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException($"ntfy answered {(int)res.StatusCode}: {why.Trim()}");
        }
    }

    private static async Task SendEmailAsync(string title, string body, PluginConfiguration cfg, CancellationToken ct)
    {
        if (cfg.EmailTo.Trim().Length == 0 || cfg.SmtpHost.Trim().Length == 0)
        {
            throw new InvalidOperationException("Set the address to send to and the mail server first.");
        }

        var from = cfg.EmailFrom.Trim().Length > 0 ? cfg.EmailFrom.Trim()
            : cfg.SmtpUser.Contains('@', StringComparison.Ordinal) ? cfg.SmtpUser.Trim() : cfg.EmailTo.Trim();
        using var msg = new MailMessage(new MailAddress(from, "JellyDVR"), new MailAddress(cfg.EmailTo.Trim()))
        {
            Subject = title,
            Body = body
        };

        // System.Net.Mail speaks STARTTLS (port 587), not SSL from the first byte (port 465).
        using var smtp = new SmtpClient(cfg.SmtpHost.Trim(), cfg.SmtpPort > 0 ? cfg.SmtpPort : 587)
        {
            EnableSsl = cfg.SmtpSsl,
            Timeout = 20000
        };
        if (cfg.SmtpUser.Trim().Length > 0)
        {
            smtp.Credentials = new NetworkCredential(cfg.SmtpUser.Trim(), cfg.SmtpPassword);
        }

        await smtp.SendMailAsync(msg, ct).ConfigureAwait(false);
    }
}
