# ЛР №9 — памятка запуска и демонстрации Kubernetes

Проект: фитнес-центр, C# TCP-сервер + PostgreSQL Primary/Replica.

## 0. Важные имена и настройки

| Ресурс | Имя / значение |
|---|---|
| Minikube context | `minikube` |
| Deployment сервера | `fitness-server` |
| Service сервера | `fitness-server` |
| Deployment БД | `db-primary`, `db-replica` |
| Service БД | `db-primary`, `db-replica` |
| ConfigMap параметра приложения | `fitness-config` |
| Переменная ConfigMap | `APP_MODE=kubernetes` |
| TCP-порт сервера | `5000` |
| Образ сервера с выводом APP_MODE | `7lab-server:mode` |
| Данные Primary / Replica | PVC `postgres-primary-data`, `postgres-replica-data` |

Все команды `kubectl` ниже выполняются из каталога проекта `9lab`, где находятся `compose.yaml` и папка `k8s/`.

**Важно перед запуском:** в `k8s/01-server.yaml` должна быть указана актуальная строка `image: 7lab-server:mode`, а внутри `env` должен быть блок `APP_MODE`, ссылающийся на ConfigMap `fitness-config`. Не возвращайте `7lab-server:latest`, иначе можно случайно запустить старый образ без вывода APP_MODE.

Проверить это можно командой:

```bash
grep -n -A5 -B2 -E 'image:|APP_MODE|fitness-config' k8s/01-server.yaml
```

Ожидается `image: 7lab-server:mode` и ссылка на `fitness-config` / ключ `APP_MODE`.

---

## 1. Как правильно выключить после работы

1. Завершить клиент командой `0` в меню.
2. В терминале с `kubectl port-forward` нажать `Ctrl+C`.
3. Остановить Minikube:

```bash
minikube stop
```

Это останавливает локальный Kubernetes-кластер, но сохраняет сам кластер, образы и данные PersistentVolumeClaim. Docker можно оставить запущенным.

**Не использовать для обычного выключения:**

```bash
minikube delete
```

`minikube delete` удаляет кластер Minikube и может привести к потере локальных данных БД. Не удаляйте PVC `postgres-primary-data` и `postgres-replica-data`, если нужно сохранить данные.

---

## 2. Как запустить перед лабораторной

Открой терминал в каталоге проекта `9lab`.

### 2.1. Запустить Docker и Minikube

```bash
sudo systemctl start docker
minikube start --driver=docker
kubectl config use-context minikube
```

Проверить узел:

```bash
kubectl get nodes
```

Узел `minikube` должен иметь статус `Ready`.

### 2.2. Применить конфигурацию БД и дождаться готовности

```bash
kubectl apply -f k8s/00-databases.yaml
kubectl rollout status deployment/db-primary --timeout=180s
kubectl rollout status deployment/db-replica --timeout=180s
```

Манифест создаёт Secret, SQL-init ConfigMap, два PVC, два Deployment и два Service для PostgreSQL.

### 2.3. Применить ConfigMap и сервер

```bash
kubectl apply -f k8s/02-configmap.yaml
kubectl apply -f k8s/01-server.yaml
kubectl rollout status deployment/fitness-server --timeout=180s
kubectl scale deployment/fitness-server --replicas=1
```

Если файлы уже применялись, повторный `kubectl apply` безопасно приводит ресурсы к описанной в YAML конфигурации.

### 2.4. Проверить состояние

```bash
kubectl get deployments
kubectl get pods -o wide
kubectl get services
kubectl exec deployment/fitness-server -- printenv APP_MODE
```

Ожидается:

- `db-primary`, `db-replica`, `fitness-server` доступны;
- один Pod `fitness-server`;
- два Pod PostgreSQL;
- `APP_MODE` выводит `kubernetes`.

Проверить строку в журнале C#:

```bash
kubectl logs deployment/fitness-server --tail=100 | grep -F 'Режим приложения'
```

Ожидается строка `Режим приложения: kubernetes`.

---

## 3. Как подключить клиент к серверу Kubernetes

Оставить один терминал открытым для перенаправления порта:

```bash
kubectl port-forward service/fitness-server 5000:5000
```

Не закрывать этот терминал во время демонстрации. Клиент запускается в другом терминале из каталога проекта:

```bash
dotnet run --project Client/Client.csproj
```

Клиент по умолчанию подключается к `127.0.0.1:5000`, поэтому `port-forward` направляет соединение в Kubernetes Service.

---

## 4. План демонстрации преподавателю

Рекомендуемый порядок — показывать пункты последовательно и сразу пояснять назначение ресурса.

### Шаг 1. Показать кластер и узел

```bash
kubectl cluster-info
kubectl get nodes -o wide
kubectl config current-context
```

Пояснить: Minikube запускает локальный одновузловой Kubernetes-кластер; `kubectl` — CLI для управления кластером.

### Шаг 2. Показать ресурсы приложения

```bash
kubectl get deployments
kubectl get pods -o wide
kubectl get services
```

Пояснить:

- Deployment поддерживает заданное количество Pod;
- Pod — экземпляр приложения;
- Service предоставляет стабильную точку доступа к Pod;
- PostgreSQL Primary и Replica — отдельные Deployment и Service.

### Шаг 3. Показать ConfigMap

```bash
kubectl get configmap fitness-config -o yaml
kubectl exec deployment/fitness-server -- printenv APP_MODE
kubectl logs deployment/fitness-server --tail=100 | grep -F 'Режим приложения'
```

Пояснить: ConfigMap хранит неконфиденциальную настройку; Deployment передаёт её контейнеру как переменную окружения; C# сервер читает и выводит значение при старте.

### Шаг 4. Показать работу клиент-серверного приложения

В одном терминале работает `kubectl port-forward`, во втором — клиент `dotnet run --project Client/Client.csproj`.

В меню клиента можно показать минимум три операции предметной области:

1. `1 — Зарегистрировать клиента` — создать клиента. Введите уникальный номер телефона.
2. `2 — Купить абонемент` — использовать тот же номер телефона и создать абонемент.
3. `3 — Забронировать тренировку` — использовать номер зарегистрированного клиента, тренера `Иванов` / `Иван` и будущие дату и время. Приложение создаёт слот при первом бронировании.
4. Дополнительно `4 — Отменить тренировку` — отменить запись на тот же слот.
5. Дополнительно `10 — Показать состояние Primary / Replica` — показать доступность двух БД.

Параллельно в третьем терминале можно показывать журналы:

```bash
kubectl logs -f deployment/fitness-server
```

В логах видны подключения, операции, транзакции и сообщения репликации.

### Шаг 5. Показать масштабирование 1 → 3 → 2 → 3

```bash
kubectl scale deployment/fitness-server --replicas=3
kubectl get pods -l app=fitness-server -w
```

Дождаться трёх Pod, нажать `Ctrl+C`, затем:

```bash
kubectl scale deployment/fitness-server --replicas=2
kubectl get pods -l app=fitness-server
kubectl scale deployment/fitness-server --replicas=3
kubectl rollout status deployment/fitness-server
kubectl get pods -l app=fitness-server
```

Пояснить: `replicas` задаёт желаемое количество экземпляров, Deployment поддерживает его.

### Шаг 6. Показать восстановление после удаления Pod

При трёх экземплярах:

```bash
POD=$(kubectl get pods -l app=fitness-server -o jsonpath='{.items[0].metadata.name}')
echo "Удаляем Pod: $POD"
kubectl delete pod "$POD"
kubectl get pods -l app=fitness-server -w
```

Дождаться, пока снова будет три Pod, затем `Ctrl+C`.

Пояснить: удалённый Pod заменяется контроллером Deployment, чтобы вернуть желаемое количество экземпляров.

После эксперимента вернуть один экземпляр:

```bash
kubectl scale deployment/fitness-server --replicas=1
kubectl rollout status deployment/fitness-server
```

### Шаг 7. Показать диагностику и журналы

```bash
kubectl describe deployment fitness-server
kubectl get events --sort-by=.metadata.creationTimestamp
kubectl logs deployment/fitness-server --tail=100
kubectl logs deployment/db-primary --tail=50
kubectl logs deployment/db-replica --tail=50
kubectl get endpoints fitness-server
```

Если демонстрируется намеренная ошибка образа, временно укажите заведомо неправильный тег, примените Deployment, найдите `ErrImagePull` / `ImagePullBackOff` в `kubectl get pods` и причину в разделе `Events` команды `kubectl describe pod <имя-pod>`. После фиксации обязательно верните `7lab-server:mode` в YAML и восстановите Deployment.

Примечание по логам: у сервера TCP-протокол, а Kubernetes `startupProbe` / `readinessProbe` / `livenessProbe` проверяют TCP-порт. Поэтому проверки могут отображаться в журнале сервера как короткие подключения клиентов. Это не обязательно реальные запуски клиента.

---

## 5. Запуск имеющихся Unit- и интеграционных тестов

В `Tests/Program.cs` есть Unit-тесты Pub/Sub и Consensus, затем интеграционные проверки TCP-сервера, Primary/Replica, транзакций, конкурентного бронирования, failover и восстановления. Тестовая программа должна обращаться к серверу и обеим БД по именам Kubernetes Service.

Собрать и загрузить тестовый образ (делается один раз или после изменения кода тестов):

```bash
docker build -t 7lab-tests:latest --build-arg APP_PROJECT=Tests .
minikube image load 7lab-tests:latest
```

Создать Job-файл:

```bash
cat > k8s/03-tests-job.yaml <<'YAML'
apiVersion: batch/v1
kind: Job
metadata:
  name: fitness-tests
spec:
  backoffLimit: 0
  template:
    spec:
      restartPolicy: Never
      containers:
        - name: tests
          image: 7lab-tests:latest
          imagePullPolicy: IfNotPresent
          env:
            - name: SERVER_HOST
              value: fitness-server
            - name: SERVER_PORT
              value: "5000"
            - name: DB_HOST
              value: db-primary
            - name: DB_PORT
              value: "5432"
            - name: REPLICA_DB_HOST
              value: db-replica
            - name: REPLICA_DB_PORT
              value: "5432"
            - name: DB_NAME
              valueFrom:
                secretKeyRef:
                  name: fitness-db-secret
                  key: POSTGRES_DB
            - name: DB_USER
              valueFrom:
                secretKeyRef:
                  name: fitness-db-secret
                  key: POSTGRES_USER
            - name: DB_PASSWORD
              valueFrom:
                secretKeyRef:
                  name: fitness-db-secret
                  key: POSTGRES_PASSWORD
YAML
```

Перед запуском тестов оставить сервер в одном экземпляре. Затем:

```bash
kubectl scale deployment/fitness-server --replicas=1
kubectl rollout status deployment/fitness-server
kubectl delete job fitness-tests --ignore-not-found
kubectl apply -f k8s/03-tests-job.yaml
kubectl logs -f job/fitness-tests
```

В конце успешного набора программа выводит `ALL TESTS PASSED`. Если тест завершился ошибкой, посмотреть:

```bash
kubectl describe job fitness-tests
kubectl get pods -l job-name=fitness-tests
kubectl logs job/fitness-tests
```

После выполнения Job можно удалить, не затрагивая БД:

```bash
kubectl delete job fitness-tests
```

---

## 6. Что сказать при сравнении Docker Compose и Kubernetes

- Docker Compose описывает и запускает набор взаимосвязанных контейнеров на одном Docker-хосте.
- Kubernetes управляет желаемым состоянием приложения: Deployment поддерживает количество Pod, Service даёт стабильную точку доступа, ConfigMap передаёт настройки.
- В Compose можно масштабировать сервисы и задавать restart policy, но это не тот же механизм контроллеров Kubernetes, который отслеживает желаемое число реплик и создаёт новый Pod после его удаления.
- В этой работе Minikube запускает локальный кластер Kubernetes на одном компьютере; три Pod не означают три отдельные виртуальные машины.

---

## 7. После демонстрации

1. Завершить клиент (`0`).
2. Остановить `kubectl port-forward` (`Ctrl+C`).
3. Вернуть сервер к одному Pod, если демонстрация закончилась на масштабировании:

```bash
kubectl scale deployment/fitness-server --replicas=1
```

4. Остановить кластер:

```bash
minikube stop
```

Для следующего занятия повторить раздел **2. Как запустить перед лабораторной**.

**Не выполнять `minikube delete` и не удалять PVC**, если требуется сохранить состояние баз данных между занятиями.
