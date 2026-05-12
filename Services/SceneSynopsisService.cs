using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Novalist.Sdk.Services;
using SdkAiChatMessage = Novalist.Sdk.Services.AiChatMessage;

namespace Novalist.Extensions.AiAssistant.Services;

/// <summary>
/// Generates a one-to-two-sentence synopsis for a scene by sending its prose
/// to the configured AI model and writing the result back to
/// <see cref="IExtensionProjectService.SetSceneSynopsisAsync"/>.
/// </summary>
public sealed class SceneSynopsisService
{
    private readonly AiService _ai;
    private readonly IHostServices _host;
    private readonly IExtensionLocalization _loc;

    public SceneSynopsisService(AiService ai, IHostServices host, IExtensionLocalization loc)
    {
        _ai = ai;
        _host = host;
        _loc = loc;
    }

    public async Task<string?> GenerateAndSaveAsync(string chapterGuid, string sceneId, CancellationToken cancellationToken = default)
    {
        using var progress = _host.ShowBusyProgress(new BusyProgressOptions
        {
            Title = _loc.T("synopsis.starting"),
            InitialStatus = _loc.T("synopsis.readingScene"),
            IsIndeterminate = true,
            AllowCancel = true,
        });

        // Combine the host's cancel signal with the caller's token.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, progress.CancellationToken);
        var ct = linkedCts.Token;

        try
        {
            var content = await _host.ProjectService.ReadSceneContentAsync(chapterGuid, sceneId).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(content))
            {
                progress.Dispose();
                _host.ShowNotification(_loc.T("synopsis.emptyScene"));
                return null;
            }

            var plain = StripHtml(content);
            if (plain.Length > 8000) plain = plain[..8000];

            progress.SetStatus(_loc.T("synopsis.callingAi"));

            var sys = $"You write a short, accurate synopsis of a single scene from a novel — at most two sentences. Capture what happens, who is involved, and the change of state by the end of the scene. Do not invent details. No preamble, no quotes, no commentary. Respond in {_ai.LanguageName}.";
            var messages = new List<SdkAiChatMessage>
            {
                new() { Role = "system", Content = sys },
                new() { Role = "user", Content = $"SCENE TEXT:\n{plain}" },
            };

            var result = await _ai.GenerateChatAsync(messages, cancellationToken: ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var synopsis = (result.Response ?? string.Empty).Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(synopsis))
            {
                progress.Dispose();
                _host.ShowNotification(_loc.T("synopsis.emptyResult"));
                return null;
            }

            progress.SetStatus(_loc.T("synopsis.saving"));
            await _host.ProjectService.SetSceneSynopsisAsync(chapterGuid, sceneId, synopsis).ConfigureAwait(false);

            progress.Dispose();
            _host.ShowNotification(_loc.T("synopsis.success"));
            return synopsis;
        }
        catch (OperationCanceledException)
        {
            progress.Dispose();
            _host.ShowNotification(_loc.T("synopsis.cancelled"));
            return null;
        }
        catch (Exception ex)
        {
            progress.Dispose();
            _host.ShowNotification(string.Format(_loc.T("synopsis.failedReason"), ex.Message));
            return null;
        }
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var sb = new System.Text.StringBuilder(html.Length);
        bool inTag = false;
        foreach (var ch in html)
        {
            if (ch == '<') { inTag = true; continue; }
            if (ch == '>') { inTag = false; sb.Append(' '); continue; }
            if (!inTag) sb.Append(ch);
        }
        return System.Net.WebUtility.HtmlDecode(sb.ToString());
    }
}
