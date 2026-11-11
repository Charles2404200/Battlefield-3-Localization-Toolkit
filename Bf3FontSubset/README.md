# Dựng font tiếng Việt cho BF3 Steam

Tool C# nhận ba GFX đã bổ sung đầy đủ glyph tiếng Việt ở `dump/fonts/en/` và `vietnamese_map.json` do `Bf3TextDump --map-vietnamese` tạo. Nó giữ các glyph cần dùng, đổi mã của 19 glyph sang các vị trí byte thấp đã xác minh, rồi đóng gói thành ba CAS payload có đúng kích thước buffer gốc. Font trong game được hook C++ x86 nạp từ `Battlefield 3/fonts/`; không ghi sửa archive UI.

```powershell
dotnet run --project Bf3FontSubset -c Release -- --manifest dump/fonts/en/manifest.json --corpus translations/us_loc_en_vi.csv --vietnamese-map build/Loc/vietnamese_map.json --output-dir build/fonts/full-map-stage
```

Kết quả dùng để cài ở `build/fonts/full-map-stage/vietnamese-metadata/*.cas-payload`. Tool kiểm tra glyph nguồn, glyph sau ánh xạ, cấu trúc GFX, kích thước payload và việc giải nén khớp dữ liệu. Nếu GFX thiếu chữ Việt, bổ sung glyph vào nguồn rồi chạy lại; GFX nguyên bản của BF3 không đủ.

Để xuất lại font nguồn từ game, dùng `Bf3FontDump`; xem [hướng dẫn GUI](../Bf3VietHoaGui/README.md) để xuất và cài cùng text. Các profile thử nghiệm cũ trong mã nguồn chỉ dành cho việc phân tích, không dùng cho bản dịch hiện tại.
