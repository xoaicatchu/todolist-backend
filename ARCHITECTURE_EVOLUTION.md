# Báo cáo Chuyển đổi Kiến trúc: Từ V1 nguyên thuỷ lên V2 (Chịu tải 5000+ CCU)

Hành trình giải cứu hệ thống khỏi cái chết lâm sàng ở mốc 1000 CCU để vươn lên mốc siêu chịu tải 5000 CCU (và sống sót trước Tsunami 20K CCU) là sự kết hợp của nhiều kĩ thuật tối ưu từ Network, Database cho đến Application Layer.

---

## 🏗️ 1. Các "Vũ Khí Bỏ Túi" đằng sau hệ thống V2

Hệ thống hiện tại (V2) sống sót và phản hồi chớp nhoáng là nhờ 5 sự thay đổi công nghệ mang tính cốt lõi sau:

### A. Kiến trúc Bất Đồng Bộ với RabbitMQ (Asynchronous Event-Driven)
- **V1 (Đồng bộ):** User bấm Sync. API nhận request, chọc xuống Database lưu từng dòng, lưu Redis, gài Outbox... User phải chờ toàn bộ quá trình này hoàn tất. Nếu 2000 người cùng làm, DB treo cứng (Database Lock/Timeout).
- **V2 (Bất đồng bộ):** API nhận request, đóng gói thành Message `SyncPushMessage` ném qua RabbitMQ rồi lập tức báo với app điện thoại: `202 Accepted` ("Đã xí chỗ thành công, cứ đi chơi đi, server tự lo"). Tốc độ ném message chỉ tốn ~10ms.

### B. Consumer Batching & Zero-Overhead Inserts (Của MassTransit)
- **V1:** Cứ 1 user Push là 1 Transaction mở ở DB. 1000 users = 1000 Transactions đè đè lên nhau.
- **V2:** Một con bot thầm lặng (`SyncEventConsumer`) bọc ở đầu ra của RabbitMQ. Nó không lấy từng message mà vơ vét một chùm (Batch) tối đa 100 messages của cùng 1 user (nhờ cơ chế grouping), nhét toàn bộ vào **duy nhất 01 Database Transaction**. Điều này biến 100 thao tác rề rà thành 1 thao tác sấm sét đối với PostgreSQL.

### C. Connection Pooling + PgBouncer Compatibility
- **V1:** Cấu hình `Pooling=false`. 5000 request tới là 5000 lần hệ thống phải đàm phán bắt tay "TCP Handshake" với PgBouncer. Lãng phí 15-20ms cho mỗi request vô nghĩa.
- **V2:** Kích hoạt `Pooling=true` nhưng vô hiệu hoá lệnh reset config của Npgsql. Cách này lừa được PgBouncer (Transaction Mode) cho phép API tái sử dụng hàng trăm Connection đang mở sẵn (giống như đi xa lộ không bị trạm thu phí bắt dừng lại).

### D. Tiệt triệt "Sát Thủ Vô Hình" N+1 Queries
- **V1:** Mảng 50 công việc bị đổi chỗ (Reorder) làm phát sinh 50 vòng lặp `GetByIdAsync()` xuống DB. Chậm khủng khiếp.
- **V2:** Sử dụng Bulk Fetch `GetByIdsAsync(...)`. Dùng đúng 1 query móc toàn bộ 50 công việc lên RAM bộ nhớ, xếp lại trên RAM, và đẩy lệnh `SaveChangesAsync` xuống lại DB.

### E. Tối ưu Hoá Caching Eventual Consistency (Redis)
- Thay vì để Caching 30s của hàm Pull gây sai lệch dữ liệu quá lâu, V2 giảm xuống mức điểm ngọt (Sweet-spot) là **5 giây**. Client có F5 cháy máy thì DB vẫn không hề hấn gì, mà dữ liệu trên máy nhánh khác vẫn nhảy đồng bộ gần như tức thời.

---

## ⚠️ 2. Yếu Điểm Còn Lại Cần Lưu Ý

Dù dũng mãnh, bản V2 này vẫn cần lưu ý một số yếu điểm về mặt thiết kế nếu đẩy lên mốc **100,000+ CCU**:

1. **Nút thắt cổ chai ở SignalR (WebSockets):** 
   - Hiện tại Consumer đang gọi thẳng HubContext để báo client tải lại dữ liệu. Dưới môi trường thực tế chạy nhiều Node App phân tán, việc giữ kết nối và Broadcast này cần rải đều tải (Scale-out) để tránh cháy RAM trên 1 instance.
2. **Single Database Node (Gánh đọc lẫn ghi):**
   - Chúng ta chỉ đang có 01 con PostgreSQL nhện chúa gánh cả lệnh Đọc (Pull) lẫn lệnh Ghi (Push-RabbitMQ dội xuống). Nếu ổ cứng bị nghẽn do traffic, luồng xử lý ghi background sẽ ứ đọng.

---

## 🚀 3. Hướng Tối Ưu Tiếp Theo (V3 - Enterprise Grade)

Để vươn đến phục vụ cực lớn cho toàn hệ thống thực tế:

1. **CQRS Thực Thụ + Read Replicas (Phân tán DB):**
   - Tách PostgreSQL ra làm 2: Master Node chuyên nhận lộc (dành cho Consumer Inserts). Read-Replicas cắm bên Application Layer để phục vụ độc quyền lệnh `/sync/pull`.
2. **SignalR Backplane với Redis:**
   - Gắn thêm `Redis Backplane` (hoặc Azure WebPubSub) để đồng bộ tin nhắn Websocket ra n-App Instances khi cấu hình Web App ở diện phân tán đa Cụm (Kubernetes).
3. **Queue Sharding cho RabbitMQ:**
   - Băm nhỏ Queue `sync-events` theo `TenantId` (chia theo Vùng địa lý hoặc KH VIP/Thường). Các luồng doanh nghiệp lớn sẽ được cấp phát Consumer riêng biệt.
