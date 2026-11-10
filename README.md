# Лабораторная работа №5 — репликация и отказоустойчивость

Проект продолжает лабораторные работы по теме «Фитнес-центр»: **C# TCP-клиенты → C# TCP-сервер → два независимых экземпляра PostgreSQL**.

## Что добавлено для ЛР №5

- `db-primary` — основное хранилище данных.
- `db-replica` — независимая резервная копия.
- После операций изменения сервер копирует согласованный снимок таблиц `trainers`, `clients`, `memberships`, `training_slots`, `trainings` из Primary в Replica.
- Сервер определяет недоступность Primary и направляет запросы в Replica.
- Клиентские пункты меню позволяют проверить состояние, смоделировать отказ и восстановить Primary.
- При Recovery изменения, сделанные во время отказа в Replica, синхронизируются обратно в Primary.
- Автотест проверяет предыдущие функции ЛР №4, репликацию, failover и recovery.

**Важно:** это учебная модель репликации на уровне приложения, а не production-кластер PostgreSQL. Для лабораторной намеренный отказ Primary моделируется состоянием в сервере; сами два экземпляра PostgreSQL остаются запущенными, чтобы можно было безопасно показать переключение и обратную синхронизацию.

## Запуск на Arch Linux

Нужны Docker Compose и .NET SDK 8 для запуска клиента на хосте:

```bash
sudo pacman -Syu docker docker-compose dotnet-sdk
sudo systemctl enable --now docker.service
```

Из каталога проекта, где находится `compose.yaml`. При переходе с ЛР №4 сначала остановите старые контейнеры, но не удаляйте том с данными:

```bash
sudo docker compose down --remove-orphans
sudo docker compose up -d --build
sudo docker compose ps
sudo docker compose logs -f server
```

Дождитесь сообщений `Primary → AVAILABLE`, `Replica → AVAILABLE` и `Сервер фитнес-центра запущен на порту 5000`.

В отдельном терминале запустите клиент:

```bash
dotnet run --project Client/Client.csproj
```

Для проверки нескольких клиентов откройте второй терминал и запустите ту же команду.

## Демонстрация ЛР №5

В клиентском меню:

1. Выберите `10` — показать состояние `Primary` и `Replica`; оба должны быть `AVAILABLE`.
2. Выберите `1` — зарегистрируйте тестового клиента. На сервере в журнале должны появиться `Replication → SUCCESS` и `Primary = Replica`.
3. Снова выберите `10` — оба экземпляра доступны.
4. Выберите `11` — искусственно отключите Primary. Сервер должен показать `Primary → UNAVAILABLE`, `Replica → AVAILABLE`.
5. Пока Primary недоступен, выполните `5` (чтение) или `1` (изменение). В журнале будет видно, что запрос направлен в Replica.
6. Выберите `12` — восстановить Primary и синхронизировать данные `Replica → Primary`.
7. Выберите `10` и затем выполните ещё одну регистрацию. После восстановления обе копии должны совпадать, а обычная репликация `Primary → Replica` должна продолжиться.

Операции `1`–`9` из предыдущей версии проекта сохранены.

## Автоматические проверки

```bash
sudo docker compose --profile test run --build --rm integration-test
```

Интеграционный тест проверяет регистрацию, транзакции `COMMIT`/`ROLLBACK`, параллельное бронирование и отмену записи, затем моделирует отказ Primary, чтение и запись через Replica, обратную синхронизацию и возобновление репликации. При успехе последняя строка вывода — `ALL TESTS PASSED`.

## Проверка данных вручную

Сравните количество клиентов на обоих экземплярах:

```bash
sudo docker compose exec db-primary psql -U fitness_user -d fitness_db -c 'SELECT COUNT(*) AS clients FROM clients;'
sudo docker compose exec db-replica psql -U fitness_user -d fitness_db -c 'SELECT COUNT(*) AS clients FROM clients;'
```

Проверить данные предметной области:

```bash
sudo docker compose exec db-primary psql -U fitness_user -d fitness_db -c 'SELECT id, last_name, first_name, phone, current_membership_id FROM clients ORDER BY id;'
sudo docker compose exec db-replica psql -U fitness_user -d fitness_db -c 'SELECT id, last_name, first_name, phone, current_membership_id FROM clients ORDER BY id;'
```

Журнал сервера:

```bash
sudo docker compose logs -f server
```

## Остановка

```bash
sudo docker compose down
```

Primary использует том `fitness_lab4_pgdata` из предыдущей лабораторной (если он существует), Replica — отдельный `fitness_lab5_replica_pgdata`. Для полного удаления обеих баз (необратимо):

```bash
sudo docker compose down -v
```

---

# Лабораторная работа №6 — взаимодействие по модели Pub/Sub

ЛР №6 добавлена поверх существующего проекта фитнес-центра. Клиент, TCP-сервер, PostgreSQL Primary/Replica и операции ЛР №5 сохранены.

## Архитектура Pub/Sub

```text
Client → Server → Publisher → Broker-1 (основной)
                           ↘ Broker-2 (резервный)

Broker → Logger
       → ScheduleService
       → NotificationService
       → AnalyticsService (добавляется во время работы)
```

Реализованы три типа событий варианта «Фитнес-центр»:

- `TrainingBooked` — клиент записался на тренировку;
- `TrainingCancelled` — клиент отменил запись;
- `MembershipPurchased` — клиент приобрёл абонемент.

Событие содержит `EventId`, `EventType`, `EntityId`, `ClientId`, `Timestamp` и словарь дополнительных данных. Сервер создаёт событие после успешного `COMMIT` и передаёт его Publisher. Publisher знает только кластер Broker и не вызывает Subscriber напрямую.

### Подписчики

| Subscriber | Подписки | Задержка | Назначение |
|---|---|---:|---|
| Logger | Все три события | 1 сек. | Журналирование |
| ScheduleService | TrainingBooked, TrainingCancelled | 3 сек. | Обработка расписания |
| NotificationService | Все три события | 2 сек. | Уведомления |
| AnalyticsService | Все три события | 0,5 сек. | Подсчёт обработанных событий; регистрируется динамически |

Получатели одного события запускаются параллельно через `Task.WhenAll`. Ошибка отдельного Subscriber перехватывается Broker и не мешает остальным получателям.

### Отказы, очередь и повторная доставка

Каждый Broker хранит свои подписки и очередь недоставленных сообщений. При отказе подписчика выполняются три попытки доставки. Если все попытки неудачны, сообщение остаётся в очереди этого Broker. После восстановления подписчика команда повторной доставки снова выполняет до трёх попыток.

Broker-1 используется первым. Если он отключён, Publisher направляет новое событие через Broker-2. Очереди Broker независимы: недоставленное сообщение остаётся в очереди того Broker, который его получил.

Очереди реализованы в памяти процесса. Это демонстрация временного хранения для лабораторной, а не постоянный журнал сообщений: после перезапуска сервера очередь очищается.

## Запуск

Для запуска используйте те же команды ЛР №5:

```bash
docker compose down --remove-orphans
docker compose up -d --build
docker compose ps
docker compose logs -f server
```

В другом терминале:

```bash
dotnet run --project Client/Client.csproj
```

Операции ЛР №5 (пункты 1–12) сохранены. Добавлены пункты 13–19 для проверки Pub/Sub.

## Демонстрация ЛР №6

1. Пункт `13` — показать список подписчиков, их подписки, количество обработанных событий, состояние Broker и размеры очередей.
2. Зарегистрировать клиента (пункт `1`), купить абонемент (пункт `2`) и забронировать тренировку (пункт `3`). В журнале сервера должны появиться `Publisher → PUBLISH`, `Broker → EVENT RECEIVED`, а также параллельные `START` / `SUCCESS` подписчиков.
3. Пункт `14` — отключить `ScheduleService`. Забронировать тренировку. `Logger` и `NotificationService` должны успешно обработать событие, а Broker после трёх неудачных попыток сохранить доставку в очереди.
4. Пункт `13` — проверить `ScheduleService: UNAVAILABLE` и `pending` больше нуля.
5. Пункт `15` — восстановить `ScheduleService`; пункт `17` — повторить доставку. Проверить, что очередь стала пустой и `ScheduleService` обработал событие.
6. Пункт `16` — динамически добавить `AnalyticsService`. Выполнить ещё одну операцию, создающую событие, и пунктом `13` проверить увеличение `handled` у AnalyticsService. Код Publisher при этом не меняется.
7. Пункт `18` — отключить Broker-1. Выполнить операцию, создающую событие, и убедиться, что публикация прошла через Broker-2 (`Last used: Broker-2`). Пункт `19` восстанавливает Broker-1.

## Автоматические проверки

```bash
docker compose --profile test run --build --rm integration-test
```

Тестовый сценарий включает проверки регистрации подписчиков, публикации событий после операций фитнес-центра, отказа `ScheduleService`, независимой работы других подписчиков, сохранения и повторной доставки события, динамической регистрации `AnalyticsService` и переключения с Broker-1 на Broker-2. В конце также выполняются проверки репликации и failover из ЛР №5.

Подробное описание требований, экспериментов и ответы на контрольные вопросы находятся в `LAB6-REPORT.md`.
