using UnityEngine.Scripting;

namespace HybridCLR
{
    // Native DHE storage uses the engine's closed generic layout and GC descriptor.
    // Owner keeps the logical object alive while only an interior Value ref is live.
    [Preserve]
    internal sealed class DheFieldCell<T>
    {
        [Preserve] public object Owner = null;
        [Preserve] public T Value = default(T);
    }
}
