# Architecture

`Core` owns models, scheduling and safe archive creation. `Service` owns unattended scheduling and execution. `Desktop` is the WPF administrative client. Operational data belongs under `C:\ProgramData\BackupManager`; secrets must be DPAPI-protected and never stored in logs.
