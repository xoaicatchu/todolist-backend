# Todolist Backend (TodoSync.Api)

Backend đồng bộ cho ứng dụng todolist theo hướng offline-first + event-based sync.

## Tech stack

- **.NET 10 / ASP.NET Core Minimal API**
- **SignalR** cho realtime thông báo thay đổi (`/hubs/sync`)
- **In-memory Event Store** + persist ra file JSON (`App_Data/state.json`)
- **CORS** mở cho frontend local/ngrok
- **System.Text.Json** để serialize/deserialize event và state

## Kiến trúc chính

- `POST /api/sync/push`: nhận danh sách event từ client, apply vào state server
- `GET /api/sync/pull?since=...`: trả todo thay đổi từ mốc thời gian
- `GET /api/sync/all`: debug/truy vấn toàn bộ todo hiện có
- SignalR hub `/hubs/sync`: phát `todosChanged` để client sync nền

## Event model

Các event hiện xử lý:
- `TODO_CREATED`
- `TODO_TOGGLED`
- `TODO_RENAMED`
- `TODO_REORDERED`
- `TODO_DELETED`
- `TODO_UPSERTED_FROM_SERVER`

## Chạy local

Yêu cầu: .NET SDK 10

```bash
cd TodoSync.Api
dotnet run
```

Mặc định server lắng nghe tại:
- `http://localhost:3000`

## Cấu trúc thư mục

- `TodoSync.Api/Program.cs` — bootstrap API, CORS, SignalR routes
- `TodoSync.Api/Services/EventStoreService.cs` — lõi xử lý event/state
- `TodoSync.Api/Models/*` — DTO và model đồng bộ
- `TodoSync.Api/Hubs/SyncHub.cs` — SignalR hub
- `TodoSync.Api/App_Data/state.json` — persisted state
