using System.Windows;
using System.Windows.Controls;
using Heartbeat.WPF.ViewModels;

namespace Heartbeat.WPF
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Closing += MainWindow_Closing;
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // 最小化到托盘而不退出
            e.Cancel = true;
            Hide();

            // 释放 ViewModel 的事件订阅
            if (DataContext is MainViewModel vm)
            {
                vm.Dispose();
            }
        }

        private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                textBox.ScrollToEnd();
            }
        }
    }
}