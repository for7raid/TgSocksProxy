using Android.Runtime;
using Microsoft.Extensions.Logging;
using Serilog;

namespace TgSocksProxy
{
    internal static class LoggerFactoryService
    {
        static LoggerFactoryService()
        {
            string logsDir = Path.Combine(Android.App.Application.Context.CacheDir!.AbsolutePath, "logs");
            Directory.CreateDirectory(logsDir);

            var log = new LoggerConfiguration()
                   .WriteTo.Sink(new LogStoreSink())
                   .CreateLogger();

            LoggerFactoryInstance = LoggerFactory.Create(builder =>
            {
                builder.AddSerilog(log);
                builder.AddDebug();
            });


            var logger = LoggerFactoryInstance.CreateLogger<MainActivity>();

            // 1. Для исключений в .NET потоках (менее критично, но добавить стоит)
            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                logger.LogCritical(args.ExceptionObject as Exception, "Критическая ошибка.");
                // Не пытаемся здесь "спасти" приложение
            };

            // 2. Для исключений в Task (например, забыли await)
            TaskScheduler.UnobservedTaskException += (sender, args) =>
            {
                logger.LogCritical(args.Exception, "Критическая ошибка.");
                args.SetObserved(); // Подавляем стандартное поведение (краш)
            };

            // 3. ГЛАВНОЕ: для исключений в Java/UI потоке
            AndroidEnvironment.UnhandledExceptionRaiser += (sender, args) =>
            {
                // Логируем ошибку
                logger.LogCritical(args.Exception, "Критическая ошибка.");


                // ОСТОРОЖНО: Говорим системе, что мы сами обработали ошибку
                // Это может предотвратить закрытие приложения, но может привести к багам.
                args.Handled = true;
            };
        }

        public static ILoggerFactory LoggerFactoryInstance { get; }
    }
}
