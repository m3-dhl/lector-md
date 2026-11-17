# Lector MD

Lector de Markdown para Windows: ligero (~1 MB), una ventana con pestañas, índice y árbol de carpetas. Solo lee, no edita.

## Qué
- Renderiza GitHub Flavored Markdown: tablas, listas de tareas, avisos `> [!NOTE]`, código resaltado y front matter.
- Pestañas en una sola ventana. Al abrir un `.md` ya abierto, va a su pestaña.
- Panel lateral (`Ctrl+B`) con **Índice** del documento y **Archivos** para navegar carpetas.
- Recarga sola al cambiar el archivo, tema claro/oscuro, `Ctrl+F` buscar, `Ctrl+P` imprimir.
- C# (.NET Framework 4.8) + WebView2, ya incluidos en Windows 10/11. Sin runtimes extra. Arranca en ~0,6 s y usa ~105 MB.
- Seguro ante documentos hostiles: sin scripts (CSP + DOMPurify), los enlaces nunca ejecutan programas, no se sale de la unidad ni salta a `\\servidor`, imágenes externas solo bajo demanda.

## Qué hacer
**Instalar:** descarga el `.zip` de [Releases](https://github.com/m3-dhl/lector-md/releases/latest), descomprímelo y ejecuta `Instalar.cmd`.

**Compilar:**
```powershell
.\build.ps1      # descarga dependencias y compila en .\out
.\install.ps1    # instala para el usuario actual y asocia .md
.\package.ps1    # opcional: genera el zip de la release en .\dist
```
Se instala sin permisos de administrador en `%LOCALAPPDATA%\Programs\MdLector`. Se desinstala desde *Configuración → Aplicaciones instaladas*.

Atajos: `Ctrl+O` abrir · `Ctrl+W` cerrar pestaña · `Ctrl+Tab` cambiar · `Alt+←` atrás · `F5` recargar.

Licencia: [MIT](LICENSE). Úsalo, modifícalo y distribúyelo libremente.

Dependencias (descargadas y verificadas por SHA-512 al compilar): [marked](https://github.com/markedjs/marked), [DOMPurify](https://github.com/cure53/DOMPurify), [highlight.js](https://github.com/highlightjs/highlight.js), [WebView2 SDK](https://www.nuget.org/packages/Microsoft.Web.WebView2).
