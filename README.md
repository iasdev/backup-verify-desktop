# Backup Verify

Aplicación de escritorio portátil para comprobar copias 3‑2‑1 en Windows x64. Compara recursos SMB del NAS con un disco NTFS conectado al PC y un espejo OneDrive cifrado con rclone crypt.

## Inicio

1. Descarga y descomprime `BackupVerify-win-x64.zip` desde [Releases](https://github.com/iasdev/backup-verify-desktop/releases). Abre `BackupVerify.exe` desde una carpeta donde puedas escribir.
2. Accede a cada recurso SMB desde el Explorador de Windows con tu usuario del NAS.
3. Copia tu `rclone.conf` existente junto al EXE o indica su ruta en `rcloneConfigPath`. Usa un remoto crypt con acceso al OneDrive. Trata ese archivo como una credencial.
4. Conecta el HDD al PC. En **Configuración → Disco USB**, elige el volumen y pulsa **Guardar**. La aplicación lo identifica por el GUID del volumen aunque Windows cambie su letra.
5. Revisa `config.json` y pulsa **Recargar** si lo has editado con la app abierta.

El ZIP incluye el runtime de .NET y `rclone.exe`.

## Configuración

Cada entrada de `backupSets` vincula un recurso compartido con sus dos espejos.

| Campo | Ejemplo |
|---|---|
| `name` | `Fotos` |
| `nasPath` | `\\nas\fotos` (en JSON, `"\\\\nas\\fotos"`) |
| `usbPath` | `Backup NAS a HDD/Fotos` (relativa a la raíz del volumen) |
| `cloudPath` | `onedrive-crypt:Fotos` |
| `testFiles` | `["2025/viaje.jpg", "2025/evento/IMG_001.jpg"]` (rutas relativas al grupo) |

## Modos

- **Rápida** compara rutas y tamaños, y cuenta archivos y carpetas por origen.
- **Completa** compara el USB con SHA‑256 y OneDrive con `rclone cryptcheck`. OneDrive debe presentar un hash compatible para cada archivo cifrado.
- **Archivos elegidos** recupera las rutas de `testFiles`.
- **Aleatorio** elige un archivo con igual probabilidad entre los archivos de los grupos seleccionados.

Las recuperaciones se guardan en `checks/<ejecución>/<grupo>/` junto al EXE con sufijos `_nas`, `_usb` y `_cloud`. La versión de OneDrive se recupera descifrada. La app compara por SHA‑256 los archivos recuperados con el original y conserva las recuperaciones correctas si falla otro origen. Ejecuta las verificaciones cuando las tareas de copia estén detenidas para comparar un conjunto estable.

## Compilación

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o dist/app
```

Para ejecutar una compilación local añade `config.json` y `rclone.exe` junto al ejecutable. GitHub Actions genera el ZIP portátil y las capturas de Windows. Las etiquetas `v*` publican una release estable.
