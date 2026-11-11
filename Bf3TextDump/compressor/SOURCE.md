The files in `source/` and `zopfli.exe` come from Google Zopfli,
commit `df1517dd8518113a245e4d203a04b8a0e02ab493`:
https://github.com/google/zopfli

`zopfli.exe` was built for Windows x86 with MSVC Release using this directory's
`CMakeLists.txt`. Its license is in `LICENSE.txt`.

The BF3 localization importer is written in C#. It calls this compressor only
when the standard .NET zlib output does not fit the original BF3 TOC slot.
