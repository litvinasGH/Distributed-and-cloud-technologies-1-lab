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
