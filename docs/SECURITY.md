# Security

Secrets must be encrypted with DPAPI. Restore extraction must reject ZIP entries outside the chosen destination. MySQL passwords are supplied through protected process environment variables, never command-line arguments or logs.
