# GUI Việt hóa Battlefield 3 Steam

Build GUI bằng lệnh bên dưới hoặc dùng gói EXE đã phát hành riêng. Ứng dụng C# WinForms tự chứa cần hai thư mục `hook` và `compressor` nằm cạnh EXE. GUI không mở game. Hãy thoát BF3 trước khi bấm **Cài bản dịch** hoặc **Khôi phục**. Bản dùng chung chuỗi trùng nội dung đã được thử trong nhiệm vụ Night Shift.

## Quy trình

1. Kiểm tra **Thư mục game** và **Thư mục dự án**. Mặc định chúng trỏ tới bản Steam và workspace hiện tại.
2. Bấm **Xuất text**. GUI tạo `dump/us_loc_en.tsv` để tra cứu và `translations/us_loc_en_vi.csv` để dịch. CSV đã có sẽ được giữ nguyên.
3. Bấm **Xuất font** một lần để tạo `dump/fonts/en/manifest.json` và ba file GFX. Bộ GFX trong dự án hiện tại đã được bổ sung glyph tiếng Việt. Nếu dùng một dự án mới, cần đưa ba GFX đã Việt hóa vào đúng đường dẫn trong manifest; GFX nguyên bản sẽ bị bộ dựng font từ chối và báo glyph còn thiếu.
4. Mở CSV và điền cột `vietnamese` bằng UTF-8. Giữ nguyên `id_hash`, các biến như `{0}` và mã điều khiển cần thiết. Để trống một ô nếu muốn giữ câu tiếng Anh.
5. Bấm **Kiểm tra CSV**, rồi **Cài bản dịch** khi BF3 đã thoát. GUI dựng lại text, bảng mã và ba payload font, sao lưu file đang cài, kiểm tra kích thước/hash rồi mới chép vào game.
6. Bạn tự mở BF3 để kiểm tra. Nếu cần quay lại trạng thái trước lần cài gần nhất, thoát game rồi bấm **Khôi phục lần cài trước**.

GUI hỗ trợ 134 chữ Việt có dấu được tạo sẵn ngoài ASCII, gồm chữ hoa, chữ thường và `Đ/đ`. Text trong CSV được chuẩn hóa về Unicode NFC trước khi mã hóa. 19 chữ ít xuất hiện nhất được gán vào các byte thấp để các chữ phụ đề thường gặp dùng byte cao. Bảng mã nằm trong `build/Loc/gui_stage/vietnamese_map.json`; ba payload nằm trong `build/fonts/gui_stage/vietnamese-metadata/`. Bản sao lưu của mỗi lần cài nằm trong `build/Loc/gui_install_backups/` của thư mục dự án.

Importer giữ nguyên ranh giới block giải nén 65.536 byte và kích thước chunk gốc 599.272 byte. Nó chọn giữa .NET zlib và Zopfli để lấp chính xác vùng nén 263.138 byte bằng các luồng zlib hoàn chỉnh. Khi text dịch dài hơn, các ID có nội dung giống hệt nhau dùng chung một bản chuỗi, nhưng vẫn giữ bản ghi ID riêng. Importer kiểm tra lại từng ID và nội dung sau khi dựng. Nếu không khớp cả hai giới hạn kích thước, GUI báo lỗi và không cài.

Hook C++ x86 nạp payload từ `Battlefield 3/fonts/` trong RAM. GUI chỉ thay `Update/Patch/Data/Win32/Loc/en.sb`, `dinput8.dll` và ba payload font; `en.toc`, `UI.toc`, `cas.cat` và CAS game không bị sửa. Nếu game đã có `dinput8.dll` khác, GUI dừng thay vì ghi đè.

## Build lại

```powershell
dotnet publish Bf3VietHoaGui -c Release -r win-x86 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o build/gui-release
```

Yêu cầu có DLL C++ x86 đã build ở `Bf3FontHookNative/build-x86/Release/dinput8.dll` khi publish để sao chép vào `build/gui-release/hook/`.
