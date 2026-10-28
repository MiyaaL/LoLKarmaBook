// WPF implicitly imports System.Windows.Shapes, whose Path type collides with
// System.IO.Path inside namespaces that import both. File-handling code in this
// module therefore always says System.IO explicitly; this alias additionally
// makes bare `Path`/`File` mean the file-system types in .cs files.
global using Path = global::System.IO.Path;
global using File = global::System.IO.File;
global using Directory = global::System.IO.Directory;

// The scoring submodule lives in LoLKarmaBook.Scoring; the namespace is imported
// module-wide because most page view models consume its calculators directly.
global using LoLKarmaBook.Scoring;
