# TheAirBlow.Solver.TelegramBot

Telegram-бот использует существующую `TheAirBlow.Solver.Library` и её `QueueSystem`. Логика платформ повторно не реализуется.

## Возможности

- `/start` и `/help`;
- ссылки на SkySmart, Skills4u, Saharina и TestEdu;
- постановка задания в общую очередь решателя;
- ожидание завершения и отправка текстового результата в Telegram;
- токен бота из переменной окружения `TELEGRAM_BOT_TOKEN` или `Telegram:BotToken`;
- необязательные учётные данные SkySmart из `SkySmart:Login` и `SkySmart:Password`.

## Настройка

Задай токен бота через переменную окружения:

```bash
export TELEGRAM_BOT_TOKEN="токен-от-BotFather"
```

Либо скопируй `appsettings.example.json` в `appsettings.json` и заполни `Telegram:BotToken`. Не добавляй рабочие токены и пароли в Git.

Для SkySmart при необходимости укажи логин и пароль в локальном `appsettings.json` или через переменные окружения `SkySmart__Login` и `SkySmart__Password`. Без них остальные платформы могут работать, но SkySmart потребует настроенной авторизации.

## Сборка и запуск

Из корня репозитория:

```bash
dotnet restore TheAirBlow.Solver.sln
dotnet build TheAirBlow.Solver.sln --configuration Release
dotnet run --project TheAirBlow.Solver.TelegramBot
```

Проект ориентирован на ту же .NET 6.0, что и существующие проекты решения.

## Примечания

- Очередь и обработчики платформ остаются в библиотеке; бот только маршрутизирует входящие ссылки, ожидает результат и форматирует его.
- Текущая библиотека использует один общий процессный `QueueSystem`. Запускай один экземпляр бота на один экземпляр очереди.
- Для корректной работы SkySmart библиотеке требуется действительный токен, который она получает через `SkySmart.Authenticate`.
