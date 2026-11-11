# Bảng dịch BF3

Mở [GUI Việt hóa](../Bf3VietHoaGui/README.md) để xuất text, xuất font, kiểm tra CSV và cài bản dịch. Bản nhập tiếng Việt dùng `--map-vietnamese`: nó gán đủ 134 chữ Việt có dấu ngoài ASCII vào bảng mã BF3, đồng thời tạo `vietnamese_map.json` cho bộ dựng font. CSV dùng Unicode UTF-8; tool chuẩn hóa chữ về NFC trước khi mã hóa. Bản Steam gốc đã được xác nhận hiển thị “CHIẾN DỊCH” đúng với text và font này.

Trong cột `vietnamese`, dấu phẩy không cần ngoặc kép khi dùng tool này: từ cột thứ ba trở đi được ghép lại thành bản dịch nếu ID và câu tiếng Anh khớp. Tool báo số dòng đã xử lý và không sửa CSV. Nếu muốn mở CSV đúng ba cột trong phần mềm bảng tính khác, vẫn nên đặt ô có dấu phẩy trong dấu ngoặc kép. Với bản dịch dài, tool thử Zopfli nếu .NET zlib không nén vừa vùng text cố định; nếu vẫn vượt, tool dừng trước khi cài.

Mở `us_loc_en_vi.csv` bằng Excel, LibreOffice hoặc trình sửa CSV hỗ trợ UTF-8. File được sắp theo thứ tự text trong game để dễ đọc các câu liên tiếp. Các mục rỗng đã được bỏ khỏi bảng dịch.

| Cột | Cách dùng |
| --- | --- |
| `id_hash` | ID ổn định của câu trong BF3. Giữ nguyên. |
| `english` | Câu gốc để tham khảo; không dùng làm khóa import. |
| `vietnamese` | Điền bản dịch UTF-8. Để trống nghĩa là giữ nguyên tiếng Anh. |

Các ký hiệu như `{0}`, `{0:d}`, `#SEC#` là biến trong game; giữ chúng trong bản dịch. `\xNN` trong câu gốc biểu thị byte điều khiển hoặc ký tự đặc biệt; giữ ký hiệu đó trong bản dịch nếu cần cùng hiệu ứng. CSV dùng dấu ngoặc kép theo chuẩn CSV để chứa dấu phẩy, dấu `"` và xuống dòng trong một ô.

Chạy kiểm tra trước khi dùng bản dịch:

```powershell
dotnet run --project Bf3TextDump -c Release -- --validate
```

Lệnh kiểm tra ID trùng/không tồn tại, đếm các ô đã dịch, và báo số câu gốc khác dữ liệu game hiện tại. Nó không sửa CSV hoặc file game. Tool dump chỉ tạo CSV khi file chưa tồn tại, nên chạy lại sẽ không ghi đè phần đã dịch.

Mỗi dòng sẽ được import bằng `id_hash`, không phụ thuộc offset trong `.sb`. Dump kỹ thuật đầy đủ, gồm cả mục rỗng và offset, nằm ở `../dump/us_loc_en.tsv`.

Bảng `translations.tsv` cũ ở thư mục game có hai câu đã dịch nhưng dùng ID/offset của bộ dump khác. Chúng chưa được tự động chuyển sang CSV mới; cần gán lại theo `id_hash` trước khi import để tránh nhầm các câu tiếng Anh trùng nhau.
