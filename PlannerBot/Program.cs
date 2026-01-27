using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

using var cts = new CancellationTokenSource();
var token = Environment.GetEnvironmentVariable("TELEGRAM_TOKEN") ??
            throw new Exception("TELEGRAM_TOKEN environment variable not set");
var bot = new TelegramBotClient(token, cancellationToken: cts.Token);
var me = await bot.GetMe();

bot.OnMessage += OnMessage;
while (true) ;
cts.Cancel();

async Task OnMessage(Message message, UpdateType update)
{
    if (message.Text is not { } text)
    {
        Console.WriteLine($"Received a message of type {message.Type}");
    }
    else if (text.StartsWith('/'))
    {
        var space = text.IndexOf(' ');
        if (space < 0) space = text.Length;
        var command = text[..space].ToLower();
        if (command.LastIndexOf('@') is > 0 and var at) // it's a targeted command
            if (command[(at + 1)..].Equals(me.Username, StringComparison.OrdinalIgnoreCase))
                command = command[..at];
            else
                return; // command was not targeted at me
        await OnCommand(command, text[space..].TrimStart(), message);
    }
    else
    {
        await OnTextMessage(message);
    }
}

async Task OnTextMessage(Message msg) // received a text message that is not a command
{
    Console.WriteLine($"Received text '{msg.Text}' in {msg.Chat}");
    await OnCommand("/start", "", msg); // for now we redirect to command /start
}

async Task OnCommand(string command, string args, Message msg)
{
    Console.WriteLine($"Received command: {command} {args}");
    switch (command)
    {
        case "/start":
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId, text: """
                    <b><u>Меню бота</u></b>:
                    /yes - Указать, что могу играть сегодня
                    /no - Указать, что не могу играть сегодня
                    /prob - Указать, что возможно могу сегодня
                    /plan - Запланировать на две недели
                    /get - Показать общий план и ближайшее пересечение
                    /set dd.mm.yyyy hh:mm - Установить время ближайшей игры
                    """, parseMode: ParseMode.Html, linkPreviewOptions: true,
                replyMarkup: new ReplyKeyboardRemove()); // also remove keyboard to clean-up things
            break;
    }
}