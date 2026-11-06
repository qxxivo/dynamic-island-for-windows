using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace IslandApp
{
    public partial class NotesPanel : UserControl
    {
        // Сообщаем главному окну, что печать закончилась и можно сворачиваться
        public event Action? EditingEnded;

        // Пока в поле стоит курсор, остров не сворачивается
        public bool IsEditing => NotesBox.IsKeyboardFocusWithin;

        private static readonly string NotesPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "IslandApp", "notes.txt");

        private readonly DispatcherTimer _saveTimer = new()
        {
            Interval = TimeSpan.FromMilliseconds(600)
        };

        public NotesPanel()
        {
            InitializeComponent();

            try
            {
                if (File.Exists(NotesPath))
                    NotesBox.Text = File.ReadAllText(NotesPath);
            }
            catch { /* не удалось прочитать, начнём с пустой заметки */ }

            Placeholder.Visibility = string.IsNullOrEmpty(NotesBox.Text)
                ? Visibility.Visible : Visibility.Collapsed;

            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                Save();
            };
        }

        private void NotesBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            Placeholder.Visibility = string.IsNullOrEmpty(NotesBox.Text)
                ? Visibility.Visible : Visibility.Collapsed;

            // Сохраняем через паузу после последней буквы, а не на каждую
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void NotesBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            _saveTimer.Stop();
            Save();
            EditingEnded?.Invoke();
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(NotesPath)!);
                File.WriteAllText(NotesPath, NotesBox.Text);
            }
            catch { /* файл занят, попробуем при следующем изменении */ }
        }
    }
}