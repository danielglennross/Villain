using System;
using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Villain
{
    // https://github.com/dotnet/runtime/issues/23405
    public static class CancellationTokenSourceExtensions
    {
        private const string CallbackPartitionsField = "_callbackPartitions";
        private const string CancellationTokenSourceParameter = "cancellationTokenSource";

        public static Task CancelAsync(this CancellationTokenSource source)
        {
            if (source.HasOrHasHadCallbacks())
            {
                return Task.Run(source.Cancel);
            }
           
            source.Cancel();
            return Task.CompletedTask;
        }

        private static bool HasOrHasHadCallbacks(this CancellationTokenSource source) => 
            GetCallbackPartitionsAsObject(source) != null;

        private static readonly Func<CancellationTokenSource, IEnumerable> GetCallbackPartitionsAsObject = 
            CreateCallbackPartitionsAccessor();

        private static Func<CancellationTokenSource, IEnumerable> CreateCallbackPartitionsAccessor()
        {
            var callbackPartitionsField =
                typeof(CancellationTokenSource)
                    .GetField(CallbackPartitionsField,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (callbackPartitionsField == null)
            {
                throw new Exception(
                    $"Failed to find the internal field: {CallbackPartitionsField}." +
                    "You may be running a different version of .Net than this hack supports.");
            }

            var cancellationTokenSource = Expression.Parameter(typeof(CancellationTokenSource), CancellationTokenSourceParameter);

            return Expression.Lambda<Func<CancellationTokenSource, IEnumerable>>(
                Expression.Convert(
                    Expression.Field(cancellationTokenSource, callbackPartitionsField),
                    typeof(IEnumerable)),
                cancellationTokenSource).Compile();
        }
    }
}
