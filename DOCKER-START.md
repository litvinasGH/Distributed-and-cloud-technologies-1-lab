# Запуск и демонстрация ЛР №5 — Arch Linux

## 1. Запуск двух хранилищ и C#-сервера

В каталоге проекта. При переходе с ЛР №4 сначала остановите старые контейнеры, сохранив том с данными:

```bash
sudo docker compose down --remove-orphans
sudo docker compose up -d --build
sudo docker compose ps
sudo docker compose logs -f server
```

Compose запускает два независимых PostgreSQL:

- `db-primary` — основное хранилище;
- `db-replica` — резервное хранилище.

Сервер ожидает готовности обеих баз. При старте он синхронизирует начальные данные из Primary в Replica.

## 2. Запуск клиентов

Установите .NET SDK 8, если он ещё не установлен:

```bash
sudo pacman -S dotnet-sdk
```

В новом терминале из каталога проекта:

```bash
dotnet run --project Client/Client.csproj
```

Для параллельных запросов можно запустить второй клиент в ещё одном терминале.

## 3. Основной сценарий репликации

1. В клиенте выберите `10`: оба экземпляра должны быть `AVAILABLE`.
2. Выберите `1` и зарегистрируйте нового клиента.
3. Посмотрите журнал сервера: после изменения должна быть запись `Replication → SUCCESS`.
4. Сравните строки таблицы `clients` в Primary и Replica (команды в разделе 6).
5. Повторите изменение ещё два раза. Для отчёта зафиксируйте состояние до и после операции в трёх строках таблицы результатов.

## 4. Failover

1. Выберите `11` в меню клиента. Это разрешённый метод моделирования из методички — изменение состояния Primary в программе.
2. Проверьте вывод: `Primary → UNAVAILABLE`, `Replica → AVAILABLE`.
3. Выберите `5`, чтобы выполнить чтение через Replica.
4. Выберите `1`, чтобы зарегистрировать тестового клиента. Во время отказа запись будет только в Replica.
5. Можно запустить второй экземпляр клиента и повторить чтение или изменение.

## 5. Recovery

1. Выберите `12`.
2. Сервер проверит доступность Primary, скопирует актуальные данные из Replica в Primary и только затем отключит режим failover.
3. Выберите `10`: оба экземпляра должны быть `AVAILABLE`.
4. Сравните данные в двух базах. Клиент, созданный во время отказа, должен присутствовать в обеих.
5. Создайте ещё одного клиента и проверьте, что обычная репликация Primary → Replica снова работает.

## 6. Ручная проверка таблиц

```bash
sudo docker compose exec db-primary psql -U fitness_user -d fitness_db -c '\dt'
sudo docker compose exec db-replica psql -U fitness_user -d fitness_db -c '\dt'
sudo docker compose exec db-primary psql -U fitness_user -d fitness_db -c 'SELECT id, last_name, first_name, phone FROM clients ORDER BY id;'
sudo docker compose exec db-replica psql -U fitness_user -d fitness_db -c 'SELECT id, last_name, first_name, phone FROM clients ORDER BY id;'
```

Чтобы следить за журналом:

```bash
sudo docker compose logs -f server
```

## 7. Автоматические тесты

```bash
sudo docker compose --profile test run --build --rm integration-test
```

Ожидаемый завершающий результат: `ALL TESTS PASSED`.

## 8. Остановка и очистка

```bash
sudo docker compose down
```

Данные сохраняются в томах. Полная очистка обеих баз (удалит данные):

```bash
sudo docker compose down -v
```
