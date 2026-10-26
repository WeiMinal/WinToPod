# Windows 启动器

`launcher.c` 从解压目录启动 `app/WinToPod.exe`，转发原始参数，以命名事件等待窗口加载。启动失败或超时会显示错误；超时不强制结束程序。`--self-check` 会等待子进程结束并传递失败状态。源码不包含音频、注册表或网络操作。

Windows：`pwsh -File tools/BuildLauncher.ps1 -Output artifacts/win-x64/WinToPod.exe`。需要 Visual Studio C++ x64 工具和 Windows SDK。

Linux：在本目录执行 `x86_64-w64-mingw32-windres launcher.rc -O coff -o ../artifacts/launcher.res`，再执行 `x86_64-w64-mingw32-gcc -std=c11 -Wall -Wextra -Werror -O2 -municode -mwindows launcher.c ../artifacts/launcher.res -o ../artifacts/win-x64/WinToPod.exe -lshell32 -luser32`。生成的中间文件与 EXE 不提交仓库。
