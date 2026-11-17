# Lector MD

Lector de Markdown para Windows: ligero (~1 MB), una ventana con pestañas, índice y árbol de carpetas. Solo lee, no edita.

## Qué
- Renderiza GitHub Flavored Markdown: tablas, listas de tareas, avisos `> [!NOTE]`, código resaltado y front matter.
- Pestañas en una sola ventana. Al abrir un `.md` ya abierto, va a su pestaña.
- Panel lateral (`Ctrl+B`) con **Índice** del documento y **Archivos** para navegar carpetas.
- Recarga sola al cambiar el archivo, tema claro/oscuro, `Ctrl+F` buscar, `Ctrl+P` imprimir.
- C# (.NET Framework 4.8) + WebView2, ya incluidos en Windows 10/11. Sin runtimes extra.

## Qué hacer
```powershell
.\build.ps1      # descarga dependencias y compila en .\out
.\install.ps1    # instala para el usuario actual y asocia .md
```
Se instala sin permisos de administrador en `%LOCALAPPDATA%\Programs\MdLector`. Se desinstala desde *Configuración → Aplicaciones instaladas*.

Atajos: `Ctrl+O` abrir · `Ctrl+W` cerrar pestaña · `Ctrl+Tab` cambiar · `Alt+←` atrás · `F5` recargar.

Dependencias: [marked](https://github.com/markedjs/marked), [DOMPurify](https://github.com/cure53/DOMPurify), [highlight.js](https://github.com/highlightjs/highlight.js), [WebView2 SDK](https://www.nuget.org/packages/Microsoft.Web.WebView2).
