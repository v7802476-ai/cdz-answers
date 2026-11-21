using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TheAirBlow.Solver.Library;

internal sealed class TelegramBotWorker : BackgroundService
{
    private const int TelegramMessageLimit = 3900;
    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TelegramBotWorker> _logger;
    private ITelegramBotClient? _bot;

    public TelegramBotWorker(
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        ILogger<TelegramBotWorker> logger)
    {
        _configuration = configuration;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")
                    ?? _configuration["Telegram:BotToken"];
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "Set TELEGRAM_BOT_TOKEN or Telegram:BotToken in configuration.");

        GlobalLogger.Instance = _loggerFactory.CreateLogger("Solver.Library");
        _ = new QueueSystem();

        var login = _configuration["SkySmart:Login"];
        var password = _configuration["SkySmart:Password"];
        if (!string.IsNullOrWhiteSpace(login) && !string.IsNullOrWhiteSpace(password))
        {
            try
            {
                SkySmart.Authenticate(login, password);
                _logger.LogInformation("SkySmart authentication completed.");
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "SkySmart authentication failed.");
            }
        }

        _bot = new TelegramBotClient(token);
        var me = await _bot.GetMe(stoppingToken);
        _logger.LogInformation("Telegram bot @{Username} started.", me.Username);

        using var receiverCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _bot.StartReceiving(
            HandleUpdateAsync,
            HandlePollingErrorAsync,
            new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() },
            receiverCancellation.Token);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        finally
        {
            receiverCancellation.Cancel();
        }
    }

    private async Task HandleUpdateAsync(
        ITelegramBotClient bot,
        Update update,
        CancellationToken cancellationToken)
    {
        if (update.Type != UpdateType.Message || update.Message?.Text is not { } text)
            return;

        var chatId = update.Message.Chat.Id;
        try
        {
            if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
            {
                await bot.SendMessage(chatId,
                    "Привет! Пришли ссылку на задание с SkySmart, Skills4u, Saharina или TestEdu.",
                    cancellationToken: cancellationToken);
                return;
            }

            if (text.StartsWith("/help", StringComparison.OrdinalIgnoreCase))
            {
                await bot.SendMessage(chatId,
                    "Отправь ссылку на задание. Я добавлю его в очередь существующей библиотеки решателя и пришлю результат, когда обработка закончится.",
                    cancellationToken: cancellationToken);
                return;
            }

            if (!TryCreateQueueItem(text, out var item, out var error))
            {
                await bot.SendMessage(chatId, error, cancellationToken: cancellationToken);
                return;
            }

            var queue = QueueSystem.Instance;
            var uuid = queue.Enqueue(item!);
            await bot.SendMessage(chatId,
                $"Задание принято в очередь. ID: {uuid}\nОжидай результат…",
                cancellationToken: cancellationToken);

            while (!queue.IsFinished(uuid))
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);

            var result = queue.GetFinishedResult(uuid);
            queue.RemoveFinishedResult(uuid);

            if (result.Failed)
            {
                await bot.SendMessage(chatId,
                    "Не удалось обработать задание. Проверь ссылку и попробуй позже.",
                    cancellationToken: cancellationToken);
                return;
            }

            foreach (var chunk in SplitMessage(FormatResult(result)))
                await bot.SendMessage(chatId, chunk, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to process Telegram update.");
            await bot.SendMessage(chatId,
                "Произошла ошибка при обработке сообщения. Попробуй ещё раз позже.",
                cancellationToken: cancellationToken);
        }
    }

    private Task HandlePollingErrorAsync(
        ITelegramBotClient bot,
        Exception exception,
        CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Telegram polling error.");
        return Task.CompletedTask;
    }

    private static bool TryCreateQueueItem(
        string message,
        out QueueSystem.QueueItem? item,
        out string error)
    {
        item = null;
        error = "Не нашёл ссылку. Отправь полную ссылку на задание.";

        var match = Regex.Match(message, @"https?://[^\s<>]+", RegexOptions.IgnoreCase);
        if (!match.Success)
            return false;

        var rawUrl = match.Value.TrimEnd('.', ',', ')', ']', '!', '?');
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return false;

        var host = uri.Host.ToLowerInvariant();
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        QueueSystem.Website website;
        string input;

        if (host == "skills4u.ru" || host.EndsWith(".skills4u.ru", StringComparison.Ordinal))
        {
            if (segments.Length < 2 || segments[0] != "school")
                return false;
            website = QueueSystem.Website.Skills4u;
            input = segments[1];
        }
        else if (host == "edu.skysmart.ru")
        {
            var taskIndex = Array.IndexOf(segments, "task");
            var studentIndex = Array.IndexOf(segments, "student");
            if (taskIndex >= 0 && taskIndex + 1 < segments.Length)
                input = segments[taskIndex + 1];
            else if (studentIndex >= 0 && studentIndex + 1 < segments.Length)
                input = segments[studentIndex + 1];
            else
                return false;
            website = QueueSystem.Website.SkySmart;
        }
        else if (host == "saharina.ru" || host.EndsWith(".saharina.ru", StringComparison.Ordinal))
        {
            website = QueueSystem.Website.Saharina;
            input = uri.ToString();
        }
        else if (host == "testedu.ru" || host.EndsWith(".testedu.ru", StringComparison.Ordinal))
        {
            website = QueueSystem.Website.TestEdu;
            input = uri.ToString();
        }
        else
        {
            error = "Эта платформа не поддерживается. Поддерживаются SkySmart, Skills4u, Saharina и TestEdu.";
            return false;
        }

        item = new QueueSystem.QueueItem { Website = website, SolverInput = input };
        error = string.Empty;
        return true;
    }

    private static string FormatResult(QueueSystem.QueueItem item)
    {
        var output = new List<string>();

        switch (item.Website)
        {
            case QueueSystem.Website.Skills4u
                when item.SolverOutput is List<Skills4u.QuestionAnswerPair> pairs:
                output.AddRange(pairs.Select((pair, index) =>
                    $"{index + 1}. {ToPlainText(pair.Question)}\nОтвет: {ToPlainText(pair.Answer)}"));
                break;

            case QueueSystem.Website.SkySmart
                when item.SolverOutput is SkySmart.AnswersAndMeta sky:
                output.Add($"Предмет: {sky.SubjectName}\nУчитель: {sky.TeacherName}");
                output.AddRange(sky.Answers.Select(answer =>
                    $"{ToPlainText(answer.Title)}\n{ToPlainText(answer.Data)}"));
                break;

            case QueueSystem.Website.Saharina or QueueSystem.Website.TestEdu
                when item.SolverOutput is IEnumerable<string> answers:
                output.AddRange(answers.Select((answer, index) =>
                    $"{index + 1}. {ToPlainText(answer)}"));
                break;

            default:
                output.Add("Решение завершено, но формат результата неизвестен.");
                break;
        }

        return string.Join("\n\n", output.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var text = Regex.Replace(html, @"<(script|style)\b[^>]*>.*?</\1>", " ",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<br\s*/?>|</(p|div|li|tr|h[1-6])\s*>", "\n",
            RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"[ \t\r\f\v]+", " ")
            .Replace("\n ", "\n")
            .Trim();
    }

    private static IEnumerable<string> SplitMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield return "Задание обработано, но текстовый результат пуст.";
            yield break;
        }

        for (var offset = 0; offset < text.Length; offset += TelegramMessageLimit)
        {
            var length = Math.Min(TelegramMessageLimit, text.Length - offset);
            yield return text.Substring(offset, length);
        }
    }
}
