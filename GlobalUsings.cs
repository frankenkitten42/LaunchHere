// UseWPF removes System.IO from the implicit usings (WPF has its own Path shape); the app uses it everywhere.
global using System.IO;
global using Path = System.IO.Path;
