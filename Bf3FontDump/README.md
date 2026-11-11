# Import font Battlefield 3

Ba file `.gfx` đã chỉnh nằm trong `dump/fonts/en/ui/static/`. Tool lấy chúng theo `dump/fonts/en/manifest.json`, so với font gốc, rồi đóng gói các file thay đổi vào CAS mới. File gốc trong `Data/cas_*.cas` không bị ghi đè.

Chạy từ thư mục workspace:

```powershell
dotnet run --project Bf3FontDump -c Release -- --import
```

Lệnh trên tạo bản thử **chỉ để phân tích offline** tại `build/fonts/` và kiểm tra từng bản ghi CAS, catalog, `UI.sb` và SHA-1 bundle trong `UI.toc`. **Không cài bản thử này vào retail BF3**: ba lần thử trên bản game hiện tại đều hiện “Your game data is corrupt”. Tùy chọn `--install` đã bị khóa. Có thể dump bản đang cài sang thư mục khác để đối chiếu mà không ghi đè font đã chỉnh:

```powershell
dotnet run --project Bf3FontDump -c Release -- -o build/fonts/readback
```

`build/fonts/backup/UI.sb`, `UI.toc` và `cas.cat` là ba file gốc. Nếu cần khôi phục, đóng game rồi sao chép ba file này về `Update/Patch/Data/Win32/UI.sb`, `Update/Patch/Data/Win32/UI.toc` và `Update/Patch/Data/cas.cat`. Các CAS mới không còn được tham chiếu khi catalog gốc được khôi phục.

## Offset cần cập nhật

Mỗi asset mới nằm trong `cas_XX.cas` với header 32 byte `FA CE 0F F0`, tiếp theo là SHA-1 20 byte, kích thước payload `uint32 LE`, bốn byte 0. Offset trong `cas.cat` trỏ **sau** header này, tại đầu payload nén. Payload gồm các block có `decoded_size:uint32 BE`, `packed_size:uint32 BE`, rồi zlib. Mỗi entry `cas.cat` chứa SHA-1, offset, kích thước payload và số CAS.

Trong `UI.sb`, entry RES của từng font có SHA-1, `size` (payload nén) và `originalSize` (GFX giải nén). Tool sửa các trường này tại chỗ; kích thước `UI.sb` giữ nguyên. `UI.toc` lưu SHA-1 của từng vùng bundle trong `UI.sb`; tool tính lại ba checksum bị ảnh hưởng và xác minh tất cả bundle sau khi sửa. Ba entry tiếng Anh của bản game hiện tại bắt đầu tại `0x10DD` (Store), `0x1E7C` (Main UI) và `0x21F01` (EULA). Tool tìm theo tên bundle/asset và xác minh SHA-1 gốc, nên không phụ thuộc cứng vào ba offset này.

Các bản thử trước lần lượt thiếu header CAS 32 byte và checksum mới trong `UI.toc`. Bản sau đã sửa cả hai nhưng game vẫn từ chối. `UI.toc` và `cas.cat` có lớp chữ ký/XOR; mã hiện tại chỉ giữ lại header chữ ký gốc, chưa tạo được chữ ký hợp lệ cho nội dung mới. Đây là nguyên nhân cần kiểm chứng thêm, nên tool không cho cài. Cấu trúc GFX đã sửa vẫn đọc được: tag `DefineFont3` và bảng offset glyph không vượt khỏi ranh giới tag.
