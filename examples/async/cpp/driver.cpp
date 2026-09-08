// Drives the async sample on the event loop. The language runs tick sequentially; the host
// can spawn two ticks concurrently and they complete in timer order.
#include "generated.hpp"
#include <iostream>
using namespace gen;
using namespace treb;

static std::string join(const Vector<std::string>& v) {
    std::string s;
    for (const auto& x : v) { if (!s.empty()) s += ", "; s += x; }
    return s;
}

int main() {
    std::cout << "sequential=" << join(Async::demo().get()) << "\n";

    auto log = cellCreate(Vector<std::string>{});
    auto a = spawn(Async::tick(log, "a", 30));
    auto b = spawn(Async::tick(log, "b", 10));
    a.get();
    b.get();
    std::cout << "concurrent=" << join(log.get()) << "\n";
    return 0;
}
