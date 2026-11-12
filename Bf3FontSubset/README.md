# Dựng font tiếng Việt cho BF3 Steam

Tool C# nhận ba GFX đã bổ sung đầy đủ glyph tiếng Việt ở `dump/fonts/en/` và `vietnamese_map.json` do `Bf3TextDump --map-vietnamese` tạo. Nó giữ các glyph cần dùng, đổi mã của 19 glyph ít gặp nhất sang các vị trí byte thấp, rồi đóng gói thành ba CAS payload có đúng kích thước buffer gốc. Một số đường hiển thị trong game có thể xử lý byte thấp như mã điều khiển; cần thử phụ đề trong game sau khi cài. Font được hook C++ x86 nạp từ `Battlefield 3/fonts/`; không ghi sửa archive UI.

```powershell
dotnet run --project Bf3FontDump -c Release -- "C:\Program Files (x86)\Steam\steamapps\common\Battlefield 3" -o build/fonts/original_reference
dotnet run --project Bf3FontSubset -c Release -- --manifest dump/fonts/en/manifest.json --corpus translations/us_loc_en_vi.csv --vietnamese-map build/Loc/vietnamese_map.json --ascii-reference-dir build/fonts/original_reference --output-dir build/fonts/full-map-stage
```

Kết quả dùng để cài ở `build/fonts/full-map-stage/vietnamese-metadata/*.cas-payload`. Tool kiểm tra glyph nguồn, glyph sau ánh xạ, cấu trúc GFX, kích thước payload và việc giải nén khớp dữ liệu. Nếu GFX thiếu chữ Việt, bổ sung glyph vào nguồn rồi chạy lại; GFX nguyên bản của BF3 không đủ.

Để xuất lại font nguồn từ game, dùng `Bf3FontDump`; xem [hướng dẫn GUI](../Bf3VietHoaGui/README.md) để xuất và cài cùng text. Các profile thử nghiệm cũ trong mã nguồn chỉ dành cho việc phân tích, không dùng cho bản dịch hiện tại.
