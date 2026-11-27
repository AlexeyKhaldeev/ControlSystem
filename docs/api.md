# API v1 и модель данных

## Общие правила

HTTPS, JSON camelCase, UTF-8, UTC ISO 8601. Enum status сериализуется строкой Online/Offline.
Метрики в bytes, seconds и процентах. Максимум body 65 536 bytes и 32 диска.
Authorization: `Bearer <token>`; query-string credentials не принимаются.
Каждый агент заранее получает UUID + отдельный 256-bit random token. Серверу передаётся
только его SHA256 в `Security:Agents`. Регистрация/ротация API пока отсутствуют.

| Метод | Путь | Доступ | Результат |
|---|---|---|---|
| POST | `/api/v1/heartbeats` | Agent bearer, DeviceId совпадает с credential | 200, HeartbeatResponse |
| GET | `/api/v1/devices` | Operator bearer | 200, массив DeviceResponse, без pagination для <=100 ПК |
| GET | `/health/live` | Anonymous HTTPS | 200: процесс работает |
| GET | `/health/ready` | Anonymous HTTPS | 200: БД/схема доступны; иначе 503 |
| GET | `/login` | Anonymous HTTPS | HTML форма + antiforgery cookie |
| POST | `/login` | Token в form body + antiforgery | 302 на `/`, secure operator cookie |
| POST | `/logout` | Operator cookie + antiforgery | Отзыв текущей серверной UI-сессии, удаление cookie, 302 на `/login` |
| GET | `/` | Operator cookie | Blazor UI; без cookie — 302 на `/login` |
| POST | `/_blazor/negotiate?negotiateVersion=1` | Operator cookie | 200, SignalR negotiation JSON; без cookie — 401 |
| — | `/_blazor/*` | Operator cookie | Внутренний SignalR Blazor, не API для агентов |

## HeartbeatRequest

```json
{
  "protocolVersion": 1,
  "deviceId": "7765a61e-4ed5-4a86-a4ce-b75e88eb1e31",
  "hostname": "EXHIBIT-01",
  "operatingSystem": "Microsoft Windows 11",
  "agentVersion": "0.1.0",
  "sampledAtUtc": "2026-11-22T10:00:00Z",
  "isSimulated": false,
  "telemetry": {
    "cpuUsagePercent": 24.5,
    "memoryTotalBytes": 17179869184,
    "memoryAvailableBytes": 8589934592,
    "uptimeSeconds": 86400,
    "disks": [
      { "name": "C:\\", "totalBytes": 549755813888, "freeBytes": 225485783040 }
    ]
  }
}
```

Hostname 1–120, OS 1–200, agentVersion 1–40 символов; все обязательны.
cpuUsagePercent null только при прогреве/нет sample либо finite 0–100. Total RAM >0,
0 <= available <= total. Uptime от 0 до 100 лет. Диски — массив 0–32,
уникальные имена 1–200, total >0, 0 <= free <= total. Отклонение sampledAtUtc от
часов сервера не более 5 минут. protocolVersion должен равняться 1.
isSimulated информационный: серверу нельзя считать его доказательством реального Windows.

Ответ:

```json
{ "receivedAtUtc": "2026-11-22T10:00:01Z", "nextHeartbeatSeconds": 10 }
```

Текущий агент использует зафиксированный интервал протокола 10 s; динамическая смена
интервала по ответу пока не реализована. Ответ отправляется только после успешного upsert.
DTO обновления всегда полный: PATCH/delta и накопленной очереди нет.

## DeviceResponse

Поля запроса без protocolVersion плюс:

| Поле | Тип | Значение |
|---|---|---|
| lastSeenAtUtc | DateTimeOffset | Время получения сервером |
| status | enum string | Online, если прошло <=35 s; Offline после 35 s |

`sampledAtUtc` сохраняется в UTC. Данные Offline остаются последним snapshot.
Результат отсортирован по hostname. Пустой массив означает отсутствие принятых heartbeat,
а не отсутствие provisioned credentials.

## Ошибки и доступ

| Код | Причина |
|---|---|
| 400 | HTTP вместо HTTPS, невалидный JSON/метрики/версия/время, CSRF validation |
| 401 | Нет bearer, неправильный token, неверный operator login или нет operator cookie у SignalR endpoint |
| 403 | Неподходящая роль или DeviceId не соответствует agent token |
| 413 | Превышен лимит body |
| 429 | Heartbeat >30/min/agent или login >5/min/IP |
| 500 | Ошибка сервера/хранилища; secret details в ответ не выдаются |
| 503 | Readiness не подтверждён |

Ошибки валидации — ValidationProblemDetails с errors по полям. При отсутствии cookie
страница `/` перенаправляет на `/login` (302). Служебные SignalR endpoints, включая
`POST /_blazor/negotiate`, отвечают 401 без operator cookie: в ASP.NET Core 10 это
API endpoints, для которых cookie challenge не выполняет HTTP-редирект.
Bearer-токены агента и оператора не заменяют operator cookie на SignalR endpoints;
cookie не открывает bearer-only API.
Основание: [Microsoft: Cookie login redirects are disabled for known API endpoints](https://learn.microsoft.com/en-us/aspnet/core/breaking-changes/10/cookie-authentication-api-endpoints?view=aspnetcore-10.0).

## Хранение

| Devices column | PostgreSQL | Ограничения |
|---|---|---|
| DeviceId | uuid | PK, UUID агента, не генерируется БД |
| Hostname | varchar(120) | NOT NULL |
| OperatingSystem | varchar(200) | NOT NULL |
| AgentVersion | varchar(40) | NOT NULL |
| SampledAtUtc | timestamptz | NOT NULL, время агента |
| LastSeenAtUtc | timestamptz | NOT NULL, время сервера, index |
| IsSimulated | boolean | NOT NULL |
| TelemetryJson | text | NOT NULL, serialized TelemetrySnapshot |

Credential allowlist сейчас в защищённой конфигурации, не в таблице. Статус не хранится.
Таблица `__EFMigrationsHistory` обслуживается EF. История метрик, команды и аудит в текущую схему не входят.

## Жизненный цикл UI-сессии

Cookie действует 8 часов без sliding expiration. Сервер хранит только случайный ID
UI-сессии и её expiry в памяти; logout отзывает сессию для всех использующих её вкладок
и запрещает replay сохранённой cookie. Отдельный вход создаёт независимую сессию.
После перезапуска все UI-cookie требуют нового входа. Административного revoke API нет.
Circuit проверяет сессию каждую секунду; чтение и публикация очередного snapshot дополнительно
проверяются в Home. Новые HTTP/SignalR-подключения с отозванной cookie отклоняются.
Ранее полученные данные остаются у клиента; закрытие транспортного соединения не гарантируется.

## Хранение истории v0.2

Успешный heartbeat атомарно сохраняет latest и исторический snapshot в PostgreSQL.
Существующие маршруты и DTO не изменены; endpoint чтения истории не добавлен.
Retention использует серверный LastSeenAtUtc и по умолчанию выключен.
