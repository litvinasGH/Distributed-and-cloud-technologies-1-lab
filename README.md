# Kubernetes manifests for ЛР №9

Манифесты рассчитаны на локальный Minikube и проект фитнес-центра.

## Что создаётся

- два отдельных PostgreSQL Deployment: `db-primary` и `db-replica`;
- отдельный PVC для данных каждой БД;
- ConfigMap с `db/init.sql` для первичной инициализации каждой новой БД;
- Secret с учебными параметрами подключения;
- C# сервер `fitness-server` с образом `7lab-server:latest`;
- ClusterIP Services для обеих БД и TCP-сервера.

Это сохраняет архитектуру проекта: две независимые БД PostgreSQL, между которыми синхронизацию выполняет код приложения. Это не streaming replication PostgreSQL.

## Развёртывание

Из корня проекта (папка, где находится `compose.yaml`):

```bash
minikube image load postgres:16-alpine
kubectl apply -f k8s/00-databases.yaml
kubectl get pods -w
```

Дождитесь, пока оба Pod БД будут `Running` и `Ready` (Ctrl+C завершает только наблюдение). Затем:

```bash
kubectl apply -f k8s/01-server.yaml
kubectl get deployments
kubectl get pods -o wide
kubectl get services
```

Журнал сервера:

```bash
kubectl logs deployment/fitness-server -f
```

Доступ с хоста к TCP-серверу (в отдельном терминале):

```bash
kubectl port-forward service/fitness-server 5000:5000
```

Клиент запускается локально из корня проекта:

```bash
dotnet run --project Client/Client.csproj
```

Убедитесь, что клиент обращается к `localhost:5000`. Если адрес/порт клиента не конфигурируется и сейчас заданы иначе, настройку клиента нужно поправить перед тестом.

## Диагностика

```bash
kubectl get pods
kubectl describe pod <pod-name>
kubectl logs deployment/fitness-server
kubectl get endpoints fitness-server
kubectl get events --sort-by=.metadata.creationTimestamp
```

Не удаляйте PVC, если нужно сохранить данные баз.
