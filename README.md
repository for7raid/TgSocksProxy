# TgSocksProxy

Socks5-прокси сервер для Android, который туннелирует трафик через upstream HTTPS-прокси (методом CONNECT).

## Архитектура

Проект состоит из двух компонентов:

```
TgSocksProxyLib  —  библиотека .NET 10 (net10.0)
     ↕           —  ядро Socks5 + DNS + relay
TgSocksProxy    —  Android-приложение (net10.0-android)
                     UI, foreground-сервис, сохранение настроек
```

## Схема работы

```
Клиентское приложение (Telegram, браузер...)
         │
         │ Socks5 (127.0.0.1:10180)
         ▼
┌──────────────────────────────┐
│      SocksProxy (ядро)       │
│  ┌─────────────────────┐     │
│  │  Socks5 Handshake   │     │  RFC 1928
│  │  (No Auth / UserPass)│    │  RFC 1929
│  ├─────────────────────┤     │
│  │  CONNECT → target   │     │
│  └─────────┬───────────┘     │
│            │                 │
│  ┌─────────▼───────────┐     │
│  │  DNS Resolution     │     │  3× retry + DoH (1.1.1.1)
│  │  + DNS cache (5min) │     │
│  ├─────────────────────┤     │
│  │  TLS upstream       │     │  SslStream → CONNECT
│  │  (если UseTls=true) │     │
│  ├─────────────────────┤     │
│  │  Bidirectional relay│     │  Stream.CopyAsync
│  └─────────────────────┘     │
└─────────────┬────────────────┘
              │ HTTPS CONNECT (зашифрованный)
              ▼
     Upstream HTTPS-прокси (some.proxy.ru:443)
              │
              ▼
         Целевой сервер (149.154.167.51:443)
```

## Возможности

### Socks5
- Полная реализация RFC 1928 (handshake, CONNECT, IPv4/IPv6/Domain)
- Аутентификация No Auth (0x00) и Username/Password (0x02, RFC 1929)

### Надёжный DNS
- 3 попытки системного DNS с backoff (0 / 500 / 1000 мс)
- IPv4-only (AddressFamily.InterNetwork) — без зависаний на AAAA
- DoH-фолбек через Cloudflare (1.1.1.1) напрямую по IP, без зависимости от системного DNS
- DNS-кэш с TTL (по умолчанию 5 минут)

### Upstream
- HTTPS CONNECT через прокси на 443 порту
- Поддержка TLS (SslStream) к upstream
- Basic-аутентификация на upstream (Proxy-Authorization)

### Android-приложение
- Foreground-сервис (не убивается при свёртывании)
- Уведомление с количеством активных подключений
- Старт/Стоп, статус на главном экране
- Live-лог (последние 100 строк, автоскролл)
- Форма настроек (реагирует на системную светлую/тёмную тему)

## Экраны

### Главный экран
- **Start/Stop** — запуск и остановка сервиса
- **Status** — ○ Stopped / ● Running
- **Settings** — открывает форму настроек
- **Logs** — моноширинный лог с автоскроллом (последние 100 строк)

### Настройки
- **Local SOCKS5 port** — порт, на котором слушает Socks5-сервер
- **SOCKS5 auth** — опциональный логин/пароль для входящих подключений
- **Upstream proxies** — список HTTPS-прокси:
  - Host, Port, Login, Password
  - **Primary** (RadioButton) — только один активный

### Уведомление
- Иконка с количеством активных TCP-туннелей
- Текст: хост:порт upstream-прокси
- SubText: количество активных подключений

## Технические детали

### Требования
- Android 8.0+ (API 26)
- .NET 10 SDK

### Зависимости (Android-проект)
| Пакет | Версия |
|---|---|
| `Microsoft.Extensions.Logging` | 10.0.9 |
| `Serilog` | 4.3.1 |
| `Serilog.Sinks.File` | 7.0.0 |
| `Serilog.Extensions.Logging` | 10.0.0 |
| `Microsoft.Extensions.Logging.Debug` | 9.0.0 |

### Пермишены
- `INTERNET` — работа с сетью
- `FOREGROUND_SERVICE` — foreground-сервис (Android 9+)
- `FOREGROUND_SERVICE_SPECIAL_USE` — тип сервиса (Android 14+)
- `POST_NOTIFICATIONS` — нотификации (Android 13+, запрашивается в runtime)

## Пример использования (библиотека)

```csharp
var proxy = new SocksProxy(new SocksProxyOptions
{
    LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 10180),

    UpstreamProxyEndPoint = new DnsEndPoint("proxy.example.com", 443),
    UpstreamCredentials = new ProxyCredentials("user", "pass"),
    UseTls = true,

    DnsCacheTtl = TimeSpan.FromMinutes(10),
    Logger = logger
});

proxy.ClientConnected += e =>
    Console.WriteLine($"+ {e.Host}:{e.Port} ({e.ConnectionCount} active)");

proxy.ClientDisconnected += e =>
    Console.WriteLine($"- {e.Host}:{e.Port} ({e.ConnectionCount} active)");

proxy.Start();
// ...
proxy.Stop();
```

## Состав проекта

```
TgSocksProxy/
├── TgSocksProxyLib/
│   ├── SocksProxy.cs          — ядро Socks5-прокси
│   ├── ProxySettings.cs       — модель настроек
│   └── ProxySettingsContext.cs — source-generated JSON-контекст
│
├── TgSocksProxy/
│   ├── MainActivity.cs            — главный экран
│   ├── SettingsActivity.cs        — форма настроек
│   ├── SocksProxyService.cs       — foreground-сервис
│   ├── AppResolver.cs             — определение приложений (/proc/net/tcp)
│   ├── LogStore.cs                — потокобезопасный лог (100 строк)
│   ├── LogStoreSink.cs            — Serilog sink → LogStore
│   ├── ProxySettingsProvider.cs   — сохранение/загрузка настроек
│   ├── AndroidManifest.xml
│   └── Resources/
│       ├── layout/
│       │   ├── activity_main.xml
│       │   └── activity_settings.xml
│       ├── drawable/
│       │   └── ic_proxy_arrow.xml
│       └── values/
│           ├── strings.xml
│           ├── colors.xml
│           ├── styles.xml
│           └── values-v29/styles.xml (DayNight, API 29+)
│
├── TgConsoleTest/               — консольный тест (Windows/Linux)
└── README.md
```
