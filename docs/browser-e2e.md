# Browser E2E: Dashboard и операторская сессия

Дата: 24 ноября 2026. Изолированный opt-in проект
`tests/ControlSystem.BrowserTests`; продуктовый код и API-контракты не меняются.
Он намеренно не включён в solution: обычный `dotnet test ControlSystem.sln`
не требует Chrome и настройки доверия сертификату. Browser E2E запускается отдельной командой,
без автоматического skip при недоступной среде.

## Инструмент и изоляция

Microsoft.Playwright 1.65.0 + установленный Google Chrome (`Channel=chrome`),
xUnit v3 и настоящий Kestrel HTTPS на случайном loopback-порту. Node runtime поставляется
NuGet-пакетом Playwright; npm, PowerShell и скачивание отдельного Chromium не нужны.
Основание выбора: [Playwright .NET](https://playwright.dev/dotnet/docs/intro),
[WebSocket inspection](https://playwright.dev/dotnet/docs/network).

Каждый тест запускает отдельный сервер, browser, ephemeral DataProtection и явно выбранный
Testing/InMemory store. Tokens генерируются RandomNumberGenerator только в памяти;
существующие `.local/dev.env` и operator-token не читаются. Телеметрия синтетическая,
публикуется через test-only store, без изменения маршрутов или auth policies.
Счётчик `ListAsync` отличает запрет чтений от простого скрытия DOM.

## Запуск на Mac

Нужны .NET 10 SDK, Google Chrome и доступ к NuGet при первом restore.
Из корня репозитория:

```sh
mkdir -p .local/browser-e2e
# Меняет login Keychain; macOS может запросить подтверждение/пароль.
# Экспортированный private key хранится только в игнорируемой .local.
dotnet dev-certs https --trust --export-path .local/browser-e2e/localhost.pem --format Pem --no-password
chmod 600 .local/browser-e2e/localhost.key
./scripts/browser-e2e.sh
```

Скрипт работает из любой директории, проверяет наличие PEM/key и запускает отдельный
E2E test project. Для уже подготовленного сертификата можно передать абсолютные пути:

```sh
export CONTROLSYSTEM_E2E_CERT=/absolute/path/localhost.pem
export CONTROLSYSTEM_E2E_KEY=/absolute/path/localhost.key
# Если leaf подписан отдельным CA, Node должен доверять именно CA/chain PEM:
export CONTROLSYSTEM_E2E_CA=/absolute/path/test-ca.pem
./scripts/browser-e2e.sh --logger 'console;verbosity=normal'
```

Chrome использует доверие macOS; Playwright APIRequest использует Node, поэтому скрипт
задаёт `NODE_EXTRA_CA_CERTS` (по умолчанию self-signed dev certificate; для отдельного CA —
`CONTROLSYSTEM_E2E_CA`). Явно переданный NODE_EXTRA_CA_CERTS сохраняется.
При прямом `dotnet test` нужно передать обе переменные certificate/key и NODE_EXTRA_CA_CERTS.
Не использовать `IgnoreHTTPSErrors`, `--ignore-certificate-errors`, отключение hostname
или `NODE_TLS_REJECT_UNAUTHORIZED=0`.

Нет сертификата/Chrome/NuGet/TLS trust — запуск завершается ошибкой подготовки, а не
объявляет проверки пройденными. Запуск вне sandbox нужен для Kestrel sockets и Chrome.
Для отзыва доверия созданному сертификату: `security remove-trusted-cert .local/browser-e2e/localhost.pem`.
Это не удаляет сертификат из .NET certificate store. Не применять глобальный
`dotnet dev-certs https --clean`: он может удалить сертификаты других проектов.

## Проверяемые сценарии

1. Анонимный браузер перенаправляется на login; API и SignalR negotiation возвращают 401;
   marker отсутствует, хранилище не читается.
2. Настоящий form login с antiforgery, Secure/HttpOnly/Strict cookie; новый heartbeat marker
   появляется без навигации. UI-cookie не открывает bearer-only API; tokens не входят в DOM.
3. Logout во второй вкладке общей сессии скрывает открытый Dashboard, отзывает cookie replay
   и прекращает `ListAsync` минимум на полный пятисекундный refresh tick.
4. Expiry настоящей cookie/server session: test-only TimeProvider сдвигается на 9 часов,
   provider и cookie handler используют одни часы. Реальные 8 часов ожидания не нужны;
   one-second revalidation и five-second refresh timers остаются настоящими.
5. Разрыв настоящего WebSocket: native `close(4001)` с ожиданием close event; новая открытая
   socket, новое live marker, сохранённый document sentinel и circuit-open counter доказывают
   восстановление без reload и без нового circuit.
6. Потеря сети + logout через другой context с копией cookie **или expiry**: после восстановления
   negotiation 401, новые данные не попадают в DOM/входящие WebSocket frames, чтения прекращены.
7. Независимый полноценный login не отзывается при logout другой сессии; его live updates
   и negotiation продолжают работать.
8. Отдельный сервер с недоверенным self-signed localhost-сертификатом: Chrome обязан
   отклонить navigation с `ERR_CERT_AUTHORITY_INVALID` до чтения store. Вместе с успешными
   положительными сценариями это проверяет, что TLS validation не обходится.

Всего 9 test cases: offline-сценарий параметризован logout/expiry.
В logout/expiry/offline проверках наблюдатель входящих WebSocket frames сначала обязан
увидеть положительный marker, затем не должен увидеть новый защищённый marker.
Хранятся только признаки совпадений, raw frames/cookies/tokens не записываются.

## Ограничения интерпретации

- При потере сети уже доставленная телеметрия остаётся в DOM; сервер не может отозвать
  полученные bytes или очистить disconnected browser. Проверки ищут передачу новых данных.
- Native WebSocket close моделирует разрыв transport, offline — недоступность сети.
  Это не испытание packet loss/blackhole, прокси, длительного outage или reconnect storm.
- Отзыв не является транзакцией с отправкой bytes: ответ, прошедший последнюю проверку
  непосредственно до revoke, может уже находиться в сети. После обнаружения revoke
  новые refresh-чтения должны прекращаться.
- InMemory здесь выбран явно; PostgreSQL и Windows не участвуют в Browser E2E.
- Короткий expiry эмулируется серверными часами: это проверяет ticket/registry expiry,
  а не изменение клиентских часов или восьмичасовой soak.
