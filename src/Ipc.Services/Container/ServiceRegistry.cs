namespace Ipc.Services.Container;

public sealed class ServiceRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<Type, Func<ServiceRegistry, object>> _factories = new();
    private readonly Dictionary<Type, object> _singletons = new();

    public ServiceRegistry Register<T>(Func<ServiceRegistry, T> factory) where T : class
    {
        lock (_gate)
        {
            _factories[typeof(T)] = r => factory(r);
        }

        return this;
    }

    public ServiceRegistry RegisterSingleton<T>(T instance) where T : class
    {
        lock (_gate)
        {
            _singletons[typeof(T)] = instance;
        }

        return this;
    }

    public T Resolve<T>() where T : class => (T)Resolve(typeof(T));

    public object Resolve(Type type)
    {
        lock (_gate)
        {
            if (_singletons.TryGetValue(type, out var existing))
            {
                return existing;
            }

            if (_factories.TryGetValue(type, out var factory))
            {
                return factory(this);
            }

            throw new InvalidOperationException($"Service '{type}' is not registered.");
        }
    }

    public bool IsRegistered(Type type)
    {
        lock (_gate)
        {
            return _singletons.ContainsKey(type) || _factories.ContainsKey(type);
        }
    }
}