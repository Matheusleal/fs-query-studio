namespace FSharpQueryStudio.Ui

open Avalonia.Controls
open Avalonia.Input
open Avalonia.Markup.Xaml

/// The Results/Output/Problems/Schema panel — lifted out of
/// `MainWindow.axaml` into its own `UserControl` so `DockFactory` (Task 6)
/// can hand a single live instance to the results `Tool`'s `Context`.
/// Its `DataContext` is the `ShellViewModel`, set by `MainWindow`, which
/// is also where the `ResultsGrid` columns are rebuilt from
/// `Result.Schema` (Avalonia's `DataGrid` needs concrete columns).
type ResultsView() as this =
    inherit UserControl()

    do AvaloniaXamlLoader.Load(this)

    /// Double-clicking a Problems row moves the editor caret to that
    /// diagnostic — a no-op unless `ShellViewModel.SendToEditor` is wired
    /// (see `ProblemViewModel.Navigate`).
    member private _.ProblemDoubleTapped(sender: obj, _args: TappedEventArgs) =
        match (sender :?> Control).DataContext with
        | :? ProblemViewModel as p -> p.Navigate()
        | _ -> ()
