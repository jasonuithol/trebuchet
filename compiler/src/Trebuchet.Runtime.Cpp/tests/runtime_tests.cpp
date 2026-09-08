// Property-style tests for the C++ runtime. Compiled and run by CppRuntimeTests in the
// .NET test suite; also runnable by hand: g++ -std=c++20 -I.. runtime_tests.cpp && ./a.out
#include "trebuchet.hpp"
#include <iostream>
#include <map>
#include <thread>
#include <unordered_map>

using namespace treb;

static int failures = 0;
#define CHECK(cond) do { if (!(cond)) { std::cerr << "FAIL " << __FILE__ << ":" << __LINE__ << ": " #cond "\n"; failures++; } } while (0)

struct Colliding {
    int n;
    bool operator==(const Colliding&) const = default;
};
template <> struct std::hash<Colliding> { std::size_t operator()(const Colliding&) const { return 42; } };

struct Base { virtual ~Base() = default; virtual int id() const = 0; };
struct Derived : Base { int id() const override { return 7; } };

Task<int> one() { co_return 1; }
Task<int> sleepThen(std::vector<int>* order, int ms, int tag) { co_await sleepFor(ms); order->push_back(tag); co_return tag; }
Task<int> awaitPending(Pending<int> p) { co_return co_await p; }
Task<int> nestedGet() { co_return one().get() + 10; }
Task<int> spawnAndJoin(std::vector<int>* order) {
    auto slow = spawn(sleepThen(order, 30, 1));
    auto fast = spawn(sleepThen(order, 5, 2));
    co_return (co_await slow) * 10 + (co_await fast);
}
Task<int> plusOne() { co_return (co_await one()) + 1; }
Task<int> boom() { throw Panic("boom"); co_return 0; }
Task<int> viaBoom() { co_return co_await boom(); }
Task<Result<int, std::string>> okTask() { co_return ok(3); }
Task<Result<int, std::string>> propagate() {
    auto r = co_await okTask();
    if (!r.ok) co_return ErrorValue<std::string>{*r.error};
    co_return ok(*r.value * 2);
}

int main() {
    // ---- Vector: append, get, set, sharing, equality
    {
        Vector<int> v;
        std::vector<int> ref;
        for (int i = 0; i < 5000; i++) { v = v.append(i); ref.push_back(i); CHECK(v.size() == static_cast<int>(ref.size())); }
        for (int i = 0; i < 5000; i += 97) CHECK(v.get(i) == ref[i]);
        auto updated = v.set(1234, -1).set(0, -2).set(4999, -3);
        CHECK(updated.get(1234) == -1 && updated.get(0) == -2 && updated.get(4999) == -3);
        CHECK(v.get(1234) == 1234 && v.get(0) == 0 && v.get(4999) == 4999);  // original untouched
        CHECK(vector<int>(1, 2, 3) == Vector<int>{}.append(1).append(2).append(3));
        CHECK(!(vector<int>(1, 2, 3) == vector<int>(1, 2)));
        CHECK(vector<int>(1, 2).concat(vector<int>(3)) == vector<int>(1, 2, 3));
        bool threw = false;
        try { (void)v.get(5000); } catch (const Panic&) { threw = true; }
        CHECK(threw);
    }
    // ---- Map: set, get, remove, overwrite, collisions, order-independent equality
    {
        Map<std::string, int> m;
        std::unordered_map<std::string, int> ref;
        for (int i = 0; i < 3000; i++) { auto k = "k" + std::to_string(i); m = m.set(k, i); ref[k] = i; }
        CHECK(m.size() == 3000);
        for (auto& [k, val] : ref) CHECK(m.getOr(k, -1) == val);
        CHECK(m.getOr("missing", -1) == -1);
        auto over = m.set("k5", 500);
        CHECK(over.getOr("k5", 0) == 500 && m.getOr("k5", 0) == 5 && over.size() == 3000);
        auto fewer = m;
        for (int i = 0; i < 3000; i += 2) fewer = fewer.remove("k" + std::to_string(i));
        CHECK(fewer.size() == 1500 && !fewer.containsKey("k0") && fewer.containsKey("k1") && m.size() == 3000);
        Map<Colliding, std::string> c;
        for (int i = 0; i < 40; i++) c = c.set(Colliding{i}, "v" + std::to_string(i));
        CHECK(c.size() == 40);
        for (int i = 0; i < 40; i++) CHECK(c.getOr(Colliding{i}, "?") == "v" + std::to_string(i));
        auto c2 = c.remove(Colliding{7});
        CHECK(c2.size() == 39 && !c2.containsKey(Colliding{7}) && c2.containsKey(Colliding{8}));
        Map<std::string, int> a = Map<std::string, int>{}.set("x", 1).set("y", 2);
        Map<std::string, int> b = Map<std::string, int>{}.set("y", 2).set("x", 1);
        CHECK(a == b && !(a == a.set("z", 3)));
        Set<int> s = Set<int>{}.add(1).add(2).add(1);
        CHECK(s.size() == 2 && s.contains(1) && !s.contains(3));
    }
    // ---- Option and Result conversions
    {
        Result<int, std::string> r = ok(1);
        Result<int, std::string> e = error(std::string("bad"));
        Option<int> n = None;
        Option<int> so = Some(2);
        CHECK(r.ok && !e.ok && !n.isSome() && so.isSome() && *so.v == 2);
        CHECK(getOr(mapError(e, [](const std::string& x) { return x + "!"; }), 0) == 0);
        CHECK(mapError(e, [](const std::string& x) { return x + "!"; }).error == std::optional<std::string>("bad!"));
    }
    // ---- Rc: counts and base conversion
    {
        auto d = makeRc<Derived>();
        CHECK(d.useCount() == 1);
        {
            Rc<Base> b = d;
            CHECK(d.useCount() == 2 && b->id() == 7);
        }
        CHECK(d.useCount() == 1);
        Cell<int> cell(1);
        auto alias = cell;
        alias.set(5);
        CHECK(cell.get() == 5);
    }
    // ---- Task: sequencing, exceptions, propagation
    {
        CHECK(plusOne().get() == 2);
        bool threw = false;
        try { viaBoom().get(); } catch (const Panic& p) { threw = std::string(p.what()) == "boom"; }
        CHECK(threw);
        auto p = propagate().get();
        CHECK(p.ok && *p.value == 6);
    }
    // ---- EventLoop: timers, spawn, completions from another thread, nesting, deadlock
    {
        std::vector<int> order;
        CHECK(spawnAndJoin(&order).get() == 12);
        CHECK((order == std::vector<int>{2, 1}));   // timer order, not spawn order

        Completion<int> c;
        auto pending = c.pending();
        std::thread worker([c = std::move(c)]() mutable { std::this_thread::sleep_for(std::chrono::milliseconds(10)); c.resolve(41); });
        CHECK(awaitPending(pending).get() == 41);
        worker.join();

        Completion<int> failing;
        auto failingPending = failing.pending();
        std::thread failer([f = std::move(failing)]() mutable { f.fail(std::make_exception_ptr(Panic("from worker"))); });
        bool threw = false;
        try { awaitPending(failingPending).get(); } catch (const Panic& p) { threw = std::string(p.what()) == "from worker"; }
        CHECK(threw);
        failer.join();

        CHECK(nestedGet().get() == 11);

        // a resolver dropped without resolving fails the waiter rather than hanging the loop
        bool abandoned = false;
        {
            auto orphan = [] { Completion<int> dropped; return dropped.pending(); }();
            try { awaitPending(orphan).get(); } catch (const Panic& p) { abandoned = std::string(p.what()).find("dropped") != std::string::npos; }
        }
        CHECK(abandoned);
        CHECK(EventLoop::current().pending() == 0);
    }
    // ---- Instant
    {
        auto i = Instant::parse("2026-09-05T10:30:00Z");
        CHECK(i.toString() == "2026-09-05T10:30:00Z");
        CHECK(Instant::parse("2026-09-05T10:30:00+02:00") < i);
        CHECK(Instant::parse("2026-09-05T10:30:00.500Z") > i);
    }
    if (failures == 0) std::cout << "all runtime checks passed\n";
    return failures == 0 ? 0 : 1;
}
