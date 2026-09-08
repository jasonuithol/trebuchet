// Trebuchet C++ runtime: single header, C++20, standard library only.
//
// Memory model: every value is immutable and shared by reference count. Persistent
// collections share structure through treb::Rc, a reference-counted pointer whose count is
// a plain integer by default, because the prototype runs on one loop thread, and an atomic
// when the translation unit is compiled with -DTREB_THREADS, which also puts a mutex in
// every Cell. Nothing else changes: with the switch on, values may be shared between
// threads freely, and only Cell needs a lock. Records are plain structs that copy cheaply
// because their members are Rc pointers, strings, or small scalars. Suspend lowers to treb::Task, a lazy coroutine
// scheduled by treb::EventLoop, a single-threaded loop with timers and a thread-safe post
// queue that is the only way work enters from another thread.
#pragma once

#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <deque>
#include <mutex>
#include <queue>
#include <coroutine>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <ctime>
#include <functional>
#include <iostream>
#include <memory>
#include <optional>
#include <random>
#include <sstream>
#include <stdexcept>
#include <string>
#include <tuple>
#include <utility>
#include <variant>
#include <vector>

namespace treb {

// ---------------------------------------------------------------- core

struct Unit {
    bool operator==(const Unit&) const = default;
};
inline const Unit unit{};

struct Panic : std::runtime_error {
    explicit Panic(const std::string& message) : std::runtime_error(message) {}
};

struct ArgumentError {
    std::string message;
    bool operator==(const ArgumentError&) const = default;
};

/// The language's Panic record, what supervise yields for a caught treb::Panic.
struct PanicValue {
    std::string message;
    bool operator==(const PanicValue&) const = default;
};

// ---------------------------------------------------------------- Rc: non-atomic reference counting

struct RcControl {
#ifdef TREB_THREADS
    std::atomic<long> count;
#else
    long count;
#endif
    void (*destroy)(void*);
    void* object;
};

/// Shared ownership with a plain integer count. Converts from Rc<Derived> to Rc<Base>.
template <class T>
class Rc {
    template <class U> friend class Rc;
    T* ptr_ = nullptr;
    RcControl* ctrl_ = nullptr;
#ifdef TREB_THREADS
    void retain() const { if (ctrl_) ctrl_->count.fetch_add(1, std::memory_order_relaxed); }
    void release() {
        if (ctrl_ && ctrl_->count.fetch_sub(1, std::memory_order_acq_rel) == 1) {
#else
    void retain() const { if (ctrl_) ++ctrl_->count; }
    void release() {
        if (ctrl_ && --ctrl_->count == 0) {
#endif
            ctrl_->destroy(ctrl_->object);
            delete ctrl_;
        }
        ptr_ = nullptr; ctrl_ = nullptr;
    }
public:
    Rc() = default;
    Rc(std::nullptr_t) {}
    /// Adopts a freshly allocated control block (used by makeRc).
    Rc(T* ptr, RcControl* ctrl) : ptr_(ptr), ctrl_(ctrl) {}
    Rc(const Rc& o) : ptr_(o.ptr_), ctrl_(o.ctrl_) { retain(); }
    Rc(Rc&& o) noexcept : ptr_(o.ptr_), ctrl_(o.ctrl_) { o.ptr_ = nullptr; o.ctrl_ = nullptr; }
    template <class U, class = std::enable_if_t<std::is_convertible_v<U*, T*>>>
    Rc(const Rc<U>& o) : ptr_(o.ptr_), ctrl_(o.ctrl_) { retain(); }
    Rc& operator=(const Rc& o) { if (this != &o) { release(); ptr_ = o.ptr_; ctrl_ = o.ctrl_; retain(); } return *this; }
    Rc& operator=(Rc&& o) noexcept { if (this != &o) { release(); ptr_ = o.ptr_; ctrl_ = o.ctrl_; o.ptr_ = nullptr; o.ctrl_ = nullptr; } return *this; }
    ~Rc() { release(); }
    T* get() const { return ptr_; }
    T& operator*() const { return *ptr_; }
    T* operator->() const { return ptr_; }
    explicit operator bool() const { return ptr_ != nullptr; }
#ifdef TREB_THREADS
    long useCount() const { return ctrl_ ? ctrl_->count.load(std::memory_order_relaxed) : 0; }
#else
    long useCount() const { return ctrl_ ? ctrl_->count : 0; }
#endif
    bool operator==(const Rc& o) const { return ptr_ == o.ptr_; }
};

template <class T, class... Args>
Rc<T> makeRc(Args&&... args) {
    T* obj = new T(std::forward<Args>(args)...);
    return Rc<T>(obj, new RcControl{1, [](void* p) { delete static_cast<T*>(p); }, obj});
}

// ---------------------------------------------------------------- Task: lazy coroutine for Suspend

class EventLoop;

/// The result of a suspending function. Lazy: starts when awaited, or when get() schedules
/// it on the event loop and runs the loop until it completes.
template <class T>
struct Task {
    using value_type = T;
    struct promise_type {
        std::optional<T> value;
        std::exception_ptr error;
        std::coroutine_handle<> continuation;
        bool started = false;
        Task get_return_object() { return Task{std::coroutine_handle<promise_type>::from_promise(*this)}; }
        std::suspend_always initial_suspend() noexcept { return {}; }
        struct FinalAwaiter {
            bool await_ready() noexcept { return false; }
            std::coroutine_handle<> await_suspend(std::coroutine_handle<promise_type> h) noexcept {
                auto c = h.promise().continuation;
                return c ? c : std::noop_coroutine();
            }
            void await_resume() noexcept {}
        };
        FinalAwaiter final_suspend() noexcept { return {}; }
        void return_value(T v) { value = std::move(v); }
        void unhandled_exception() { error = std::current_exception(); }
    };
    std::coroutine_handle<promise_type> handle;
    explicit Task(std::coroutine_handle<promise_type> h) : handle(h) {}
    Task(Task&& o) noexcept : handle(o.handle) { o.handle = nullptr; }
    Task(const Task&) = delete;
    Task& operator=(const Task&) = delete;
    ~Task() { if (handle) handle.destroy(); }

    bool await_ready() const noexcept { return false; }
    std::coroutine_handle<> await_suspend(std::coroutine_handle<> caller) noexcept {
        handle.promise().continuation = caller;
        handle.promise().started = true;
        return handle;  // symmetric transfer into this task
    }
    T await_resume() {
        if (handle.promise().error) std::rethrow_exception(handle.promise().error);
        return std::move(*handle.promise().value);
    }
    /// Runs the task to completion by driving the current thread's event loop. Suspensions
    /// on timers or external completions really suspend; other scheduled work runs meanwhile.
    T get();
};

// ---------------------------------------------------------------- EventLoop

/// One loop per thread. Coroutines resume on the loop thread only, which is what keeps the
/// non-atomic refcounts valid. Other threads may only call post(), which is the single
/// cross-thread entry point; Completion<T>::resolve is built on it.
class EventLoop {
public:
    using Clock = std::chrono::steady_clock;

    static EventLoop& current() {
        static thread_local EventLoop loop;
        return loop;
    }

    /// Loop thread only: resume h on a later turn.
    void schedule(std::coroutine_handle<> h) { ready_.push_back([h] { h.resume(); }); }
    /// Loop thread only: run fn on a later turn.
    void defer(std::function<void()> fn) { ready_.push_back(std::move(fn)); }
    /// Any thread: run fn on the loop thread, waking the loop if it is idle.
    void post(std::function<void()> fn) {
        {
            std::lock_guard<std::mutex> lock(mutex_);
            posted_.push_back(std::move(fn));
        }
        wake_.notify_one();
    }
    void addTimer(Clock::time_point when, std::coroutine_handle<> h) { timers_.push(Timer{when, seq_++, h}); }
    /// Loop thread only: an external operation is outstanding, so an idle loop waits instead of exiting.
    void pendingAdd() { ++pending_; }
    void pendingRemove() { --pending_; }
    int pending() const { return pending_; }

    /// One turn: runs one ready item or one due timer, or sleeps until something can happen.
    /// Returns false when nothing is ready, no timers are armed, and nothing is pending.
    bool step() {
        drainPosted();
        if (!ready_.empty()) {
            auto fn = std::move(ready_.front());
            ready_.pop_front();
            fn();
            return true;
        }
        if (!timers_.empty()) {
            if (timers_.top().when <= Clock::now()) {
                auto t = timers_.top();
                timers_.pop();
                t.handle.resume();
                return true;
            }
            waitFor(timers_.top().when);
            return true;
        }
        if (pending_ > 0) {
            waitFor(std::nullopt);
            return true;
        }
        return false;
    }

    /// Runs until there is nothing left to do.
    void run() { while (step()) {} }

    /// Schedules the task if it has not started and runs the loop until it completes.
    template <class T> T blockOn(Task<T>& task) {
        auto& p = task.handle.promise();
        if (!p.started && !task.handle.done()) {
            p.started = true;
            schedule(task.handle);
        }
        while (!task.handle.done()) {
            if (!step()) throw Panic("deadlock: a task is waiting for something that will never complete");
        }
        return task.await_resume();
    }

private:
    struct Timer {
        Clock::time_point when;
        std::uint64_t seq;
        std::coroutine_handle<> handle;
        bool operator>(const Timer& o) const { return when > o.when || (when == o.when && seq > o.seq); }
    };
    std::deque<std::function<void()>> ready_;
    std::priority_queue<Timer, std::vector<Timer>, std::greater<Timer>> timers_;
    std::mutex mutex_;
    std::condition_variable wake_;
    std::deque<std::function<void()>> posted_;
    int pending_ = 0;
    std::uint64_t seq_ = 0;

    void drainPosted() {
        std::deque<std::function<void()>> batch;
        {
            std::lock_guard<std::mutex> lock(mutex_);
            batch.swap(posted_);
        }
        for (auto& fn : batch) fn();
    }
    void waitFor(std::optional<Clock::time_point> until) {
        std::unique_lock<std::mutex> lock(mutex_);
        if (!posted_.empty()) return;
        if (until) wake_.wait_until(lock, *until);
        else wake_.wait(lock);
    }
};

template <class T> T Task<T>::get() { return EventLoop::current().blockOn(*this); }

/// co_await sleepFor(ms): suspends the coroutine on the loop's timer heap.
struct SleepAwaiter {
    EventLoop::Clock::time_point when;
    bool await_ready() const noexcept { return false; }
    void await_suspend(std::coroutine_handle<> h) const { EventLoop::current().addTimer(when, h); }
    void await_resume() const noexcept {}
};
inline SleepAwaiter sleepFor(std::int64_t ms) { return SleepAwaiter{EventLoop::Clock::now() + std::chrono::milliseconds(ms < 0 ? 0 : ms)}; }

/// co_await yield(): lets other ready work run before continuing.
struct YieldAwaiter {
    bool await_ready() const noexcept { return false; }
    void await_suspend(std::coroutine_handle<> h) const { EventLoop::current().schedule(h); }
    void await_resume() const noexcept {}
};
inline YieldAwaiter yield() { return {}; }

/// A one-shot result delivered from anywhere, usually a worker thread or an I/O callback.
/// Completion<T> is the move-only resolver: move it into the callback that will call
/// resolve() or fail(). pending() gives the awaitable side, which any coroutine on the loop
/// may co_await; it resumes on the loop thread. Dropping the resolver unresolved fails the
/// waiter with a panic instead of hanging the loop. This is the shape an extern binding
/// uses to adapt a callback-based host API to a Task.
template <class T>
struct CompletionState {
    EventLoop* loop = nullptr;
    std::optional<T> value;
    std::exception_ptr error;
    std::coroutine_handle<> waiter;
    bool done = false;  // loop thread only
};

template <class T>
class Pending {
    std::shared_ptr<CompletionState<T>> st_;
public:
    explicit Pending(std::shared_ptr<CompletionState<T>> st) : st_(std::move(st)) {}
    bool await_ready() const noexcept { return st_->done; }
    void await_suspend(std::coroutine_handle<> h) const { st_->waiter = h; }
    T await_resume() const {
        if (st_->error) std::rethrow_exception(st_->error);
        return *st_->value;
    }
};

template <class T>
class Completion {
    std::shared_ptr<CompletionState<T>> st_;
    bool settled_ = false;
    template <class F> void settle(F fill) {
        settled_ = true;
        auto st = st_;
        st->loop->post([st, fill = std::move(fill)]() mutable {
            fill(*st);
            st->done = true;
            st->loop->pendingRemove();
            if (st->waiter) st->loop->schedule(st->waiter);
        });
    }
public:
    Completion() : st_(std::make_shared<CompletionState<T>>()) {
        st_->loop = &EventLoop::current();
        st_->loop->pendingAdd();
    }
    Completion(Completion&& o) noexcept : st_(std::move(o.st_)), settled_(o.settled_) { o.settled_ = true; }
    Completion& operator=(Completion&& o) noexcept {
        if (this != &o) { abandon(); st_ = std::move(o.st_); settled_ = o.settled_; o.settled_ = true; }
        return *this;
    }
    Completion(const Completion&) = delete;
    Completion& operator=(const Completion&) = delete;
    ~Completion() { abandon(); }

    /// Any thread. Delivers the value to the waiter on the loop thread.
    void resolve(T v) { settle([v = std::move(v)](CompletionState<T>& s) mutable { s.value = std::move(v); }); }
    /// Any thread.
    void fail(std::exception_ptr e) { settle([e](CompletionState<T>& s) { s.error = e; }); }
    Pending<T> pending() const { return Pending<T>(st_); }

private:
    void abandon() {
        if (st_ && !settled_) fail(std::make_exception_ptr(Panic("completion dropped without being resolved")));
    }
};

/// A task started on the loop and left to run. Awaitable by any number of joiners, or
/// get() runs the loop until it finishes. This is host-side concurrency; the language has
/// no spawn yet because parallelism is undesigned (strategy section 6).
template <class T>
class Spawned {
    struct State {
        std::optional<Task<T>> task;
        std::optional<Task<Unit>> runner;
        std::optional<T> value;
        std::exception_ptr error;
        bool done = false;
        std::vector<std::coroutine_handle<>> waiters;
        std::shared_ptr<State> self;  // keeps the frames alive until the runner has finished
    };
    std::shared_ptr<State> st_;
    static Task<Unit> run(State* st) {
        try { st->value = co_await *st->task; }
        catch (...) { st->error = std::current_exception(); }
        st->done = true;
        auto waiters = std::move(st->waiters);
        for (auto w : waiters) EventLoop::current().schedule(w);
        EventLoop::current().defer([st] { st->self.reset(); });
        co_return unit;
    }
public:
    explicit Spawned(Task<T> task) : st_(std::make_shared<State>()) {
        st_->task.emplace(std::move(task));
        st_->runner.emplace(run(st_.get()));
        st_->self = st_;
        st_->runner->handle.promise().started = true;
        EventLoop::current().schedule(st_->runner->handle);
    }
    bool done() const { return st_->done; }
    bool await_ready() const noexcept { return st_->done; }
    void await_suspend(std::coroutine_handle<> h) const { st_->waiters.push_back(h); }
    T await_resume() const {
        if (st_->error) std::rethrow_exception(st_->error);
        return *st_->value;
    }
    T get() const {
        auto& loop = EventLoop::current();
        while (!st_->done) {
            if (!loop.step()) throw Panic("deadlock: a spawned task is waiting for something that will never complete");
        }
        return await_resume();
    }
};
template <class T> Spawned<T> spawn(Task<T> task) { return Spawned<T>(std::move(task)); }

/// The sleep builtin. Named sleep_ because the emitter escapes sleep, which POSIX takes.
inline Task<Unit> sleep_(std::int64_t ms) {
    co_await sleepFor(ms);
    co_return unit;
}

/// A value held behind a shared pointer so a record or union can contain its own type.
/// The emitter boxes every field whose type mentions the enclosing type. Converts from and
/// to T implicitly; structural equality and hashing look through the box.
template <class T>
class Box {
    Rc<T> p_;
public:
    template <class U, class = std::enable_if_t<std::is_convertible_v<U&&, T>>>
    Box(U&& v) : p_(makeRc<T>(T(std::forward<U>(v)))) {}
    const T& operator*() const { return *p_; }
    const T* operator->() const { return p_.get(); }
    operator const T&() const { return *p_; }
    bool operator==(const Box& o) const { return p_.get() == o.p_.get() || *p_ == *o.p_; }
};

inline std::size_t hashCombine(std::size_t seed) { return seed; }
template <class T, class... Rest>
std::size_t hashCombine(std::size_t seed, const T& v, const Rest&... rest) {
    seed ^= std::hash<T>{}(v) + 0x9e3779b97f4a7c15ULL + (seed << 6) + (seed >> 2);
    return hashCombine(seed, rest...);
}

// ---------------------------------------------------------------- Instant

struct Instant {
    std::int64_t millis = 0;
    auto operator<=>(const Instant&) const = default;

    static Instant parse(const std::string& s) {
        int y = 0, mo = 0, d = 0, h = 0, mi = 0, sec = 0;
        double frac = 0;
        char rest[16] = {0};
        int n = std::sscanf(s.c_str(), "%d-%d-%dT%d:%d:%d%15s", &y, &mo, &d, &h, &mi, &sec, rest);
        if (n < 6) throw Panic("Instant.parse: cannot parse \"" + s + "\"");
        std::string tail = rest;
        int offsetMinutes = 0;
        if (!tail.empty() && tail[0] == '.') {
            std::size_t i = 1;
            while (i < tail.size() && std::isdigit(static_cast<unsigned char>(tail[i]))) i++;
            frac = std::atof(("0" + tail.substr(0, i)).c_str());
            tail = tail.substr(i);
        }
        if (!tail.empty() && (tail[0] == '+' || tail[0] == '-')) {
            int oh = 0, om = 0;
            std::sscanf(tail.c_str() + 1, "%d:%d", &oh, &om);
            offsetMinutes = (oh * 60 + om) * (tail[0] == '-' ? -1 : 1);
        }
        std::tm tm{};
        tm.tm_year = y - 1900; tm.tm_mon = mo - 1; tm.tm_mday = d; tm.tm_hour = h; tm.tm_min = mi; tm.tm_sec = sec;
        std::int64_t secs = static_cast<std::int64_t>(timegm(&tm)) - offsetMinutes * 60;
        return Instant{secs * 1000 + static_cast<std::int64_t>(frac * 1000)};
    }

    static Instant now() {
        using namespace std::chrono;
        return Instant{duration_cast<milliseconds>(system_clock::now().time_since_epoch()).count()};
    }

    std::string toString() const {
        std::time_t t = millis / 1000;
        std::tm tm{};
        gmtime_r(&t, &tm);
        char buf[32];
        std::strftime(buf, sizeof buf, "%Y-%m-%dT%H:%M:%SZ", &tm);
        return buf;
    }
};

// ---------------------------------------------------------------- Option and Result

struct NoneValue {};
template <class T> struct SomeValue { T value; };

template <class T>
struct Option {
    std::optional<T> v;
    Option() = default;
    Option(NoneValue) {}
    Option(SomeValue<T> s) : v(std::move(s.value)) {}
    static Option Some(T x) { return Option{SomeValue<T>{std::move(x)}}; }
    static Option None() { return Option{}; }
    bool isSome() const { return v.has_value(); }
    bool operator==(const Option&) const = default;
};

template <class T> struct OkValue { T value; };
template <class E> struct ErrorValue { E error; };

template <class T, class E>
struct Result {
    using value_type = T;
    using error_type = E;
    bool ok = false;
    std::optional<T> value;
    std::optional<E> error;
    Result() = default;
    Result(OkValue<T> o) : ok(true), value(std::move(o.value)) {}
    Result(ErrorValue<E> e) : ok(false), error(std::move(e.error)) {}
    static Result Ok(T v) { return Result{OkValue<T>{std::move(v)}}; }
    static Result Error(E e) { return Result{ErrorValue<E>{std::move(e)}}; }
    bool operator==(const Result&) const = default;
};

template <class T> OkValue<T> ok(T v) { return OkValue<T>{std::move(v)}; }
inline OkValue<Unit> ok() { return OkValue<Unit>{unit}; }
template <class E> ErrorValue<E> error(E e) { return ErrorValue<E>{std::move(e)}; }
template <class T> SomeValue<T> Some(T v) { return SomeValue<T>{std::move(v)}; }
inline const NoneValue None{};

template <class T, class P>
T fail(const P& payload) {
    if constexpr (std::is_same_v<P, ArgumentError>) throw Panic(payload.message);
    else throw Panic("panic");
}

// ---------------------------------------------------------------- Cell

template <class T>
struct CellBox {
    T value;
#ifdef TREB_THREADS
    std::mutex gate;
    explicit CellBox(T v) : value(std::move(v)) {}
#endif
};

/// The one mutable primitive. With TREB_THREADS every operation runs under the cell's mutex
/// and an update's function runs under it exactly once, as on .NET.
template <class T>
struct Cell {
    Rc<CellBox<T>> box;
#ifdef TREB_THREADS
    explicit Cell(T initial) : box(makeRc<CellBox<T>>(std::move(initial))) {}
    T get() const { std::lock_guard<std::mutex> lock(box->gate); return box->value; }
    Unit set(T v) const { std::lock_guard<std::mutex> lock(box->gate); box->value = std::move(v); return unit; }
    template <class F> Unit update(F f) const { std::lock_guard<std::mutex> lock(box->gate); box->value = f(box->value); return unit; }
    template <class F> T getAndUpdate(F f) const { std::lock_guard<std::mutex> lock(box->gate); T old = box->value; box->value = f(old); return old; }
#else
    explicit Cell(T initial) : box(makeRc<CellBox<T>>(CellBox<T>{std::move(initial)})) {}
    T get() const { return box->value; }
    Unit set(T v) const { box->value = std::move(v); return unit; }
    template <class F> Unit update(F f) const { box->value = f(box->value); return unit; }
    template <class F> T getAndUpdate(F f) const { T old = box->value; box->value = f(old); return old; }
#endif
    bool operator==(const Cell& o) const { return box == o.box; }
};
template <class T> Cell<T> cellCreate(T v) { return Cell<T>(std::move(v)); }

// ---------------------------------------------------------------- Vector: 32-way persistent trie with tail

template <class T>
class Vector {
    static constexpr int Bits = 5;
    static constexpr int Width = 1 << Bits;
    static constexpr int Mask = Width - 1;

    struct Node {
        std::vector<Rc<Node>> children;
        std::vector<T> leaves;
    };
    using NodePtr = Rc<Node>;
    using TailPtr = Rc<std::vector<T>>;

    int count_ = 0;
    int shift_ = Bits;
    NodePtr root_;
    TailPtr tail_;

    Vector(int count, int shift, NodePtr root, TailPtr tail)
        : count_(count), shift_(shift), root_(std::move(root)), tail_(std::move(tail)) {}

    int tailOffset() const { return count_ < Width ? 0 : ((count_ - 1) >> Bits) << Bits; }

    static NodePtr newPath(int level, NodePtr node) {
        if (level == 0) return node;
        auto n = makeRc<Node>();
        n->children.resize(Width);
        n->children[0] = newPath(level - Bits, node);
        return n;
    }

    NodePtr pushTail(int level, const NodePtr& parent, NodePtr tailNode) const {
        int sub = ((count_ - 1) >> level) & Mask;
        auto result = makeRc<Node>(*parent);
        if (result->children.size() < static_cast<std::size_t>(Width)) result->children.resize(Width);
        NodePtr toInsert;
        if (level == Bits) toInsert = tailNode;
        else {
            NodePtr child = parent->children.size() > static_cast<std::size_t>(sub) ? parent->children[sub] : NodePtr{};
            toInsert = child ? pushTail(level - Bits, child, tailNode) : newPath(level - Bits, tailNode);
        }
        result->children[sub] = toInsert;
        return result;
    }

    static NodePtr doSet(int level, const NodePtr& node, int index, const T& item) {
        auto result = makeRc<Node>(*node);
        if (level == 0) result->leaves[index & Mask] = item;
        else {
            int sub = (index >> level) & Mask;
            result->children[sub] = doSet(level - Bits, node->children[sub], index, item);
        }
        return result;
    }

public:
    Vector() : root_(makeRc<Node>()), tail_(makeRc<std::vector<T>>()) {
        root_->children.resize(Width);
    }

    template <class It> static Vector from(It begin, It end) {
        Vector v;
        for (; begin != end; ++begin) v = v.append(*begin);
        return v;
    }

    int size() const { return count_; }
    bool empty() const { return count_ == 0; }

    const T& get(int index) const {
        if (index < 0 || index >= count_) throw Panic("index " + std::to_string(index) + " out of range for a vector of " + std::to_string(count_));
        if (index >= tailOffset()) return (*tail_)[index & Mask];
        const Node* node = root_.get();
        for (int level = shift_; level > 0; level -= Bits) node = node->children[(index >> level) & Mask].get();
        return node->leaves[index & Mask];
    }
    const T& operator[](int index) const { return get(index); }

    Vector append(T item) const {
        if (count_ - tailOffset() < Width) {
            auto newTail = makeRc<std::vector<T>>(*tail_);
            newTail->push_back(std::move(item));
            return Vector(count_ + 1, shift_, root_, newTail);
        }
        auto tailNode = makeRc<Node>();
        tailNode->leaves = *tail_;
        NodePtr newRoot;
        int newShift = shift_;
        if ((count_ >> Bits) > (1 << shift_)) {
            auto n = makeRc<Node>();
            n->children.resize(Width);
            n->children[0] = root_;
            n->children[1] = newPath(shift_, tailNode);
            newRoot = n;
            newShift += Bits;
        } else newRoot = pushTail(shift_, root_, tailNode);
        auto newTail = makeRc<std::vector<T>>();
        newTail->push_back(std::move(item));
        return Vector(count_ + 1, newShift, newRoot, newTail);
    }

    Vector set(int index, T item) const {
        if (index < 0 || index >= count_) throw Panic("index out of range");
        if (index >= tailOffset()) {
            auto newTail = makeRc<std::vector<T>>(*tail_);
            (*newTail)[index & Mask] = std::move(item);
            return Vector(count_, shift_, root_, newTail);
        }
        return Vector(count_, shift_, doSet(shift_, root_, index, item), tail_);
    }

    Vector concat(const Vector& other) const {
        Vector v = *this;
        for (int i = 0; i < other.count_; i++) v = v.append(other.get(i));
        return v;
    }

    struct Iterator {
        const Vector* v; int i;
        const T& operator*() const { return v->get(i); }
        Iterator& operator++() { ++i; return *this; }
        bool operator!=(const Iterator& o) const { return i != o.i; }
    };
    Iterator begin() const { return {this, 0}; }
    Iterator end() const { return {this, count_}; }

    bool operator==(const Vector& o) const {
        if (o.count_ != count_) return false;
        for (int i = 0; i < count_; i++) if (!(get(i) == o.get(i))) return false;
        return true;
    }
};

template <class T, class... Ts>
Vector<T> vector(Ts&&... items) {
    Vector<T> v;
    ((v = v.append(T(std::forward<Ts>(items)))), ...);
    return v;
}

// ---------------------------------------------------------------- Map: hash array mapped trie

template <class K, class V>
class Map {
    static constexpr int Bits = 5;
    static constexpr int Mask = (1 << Bits) - 1;

    struct Leaf { std::size_t hash; K key; V value; };
    struct Node;
    using NodePtr = Rc<Node>;
    using Slot = std::variant<Leaf, NodePtr>;
    struct Node {
        std::uint32_t bitmap = 0;
        std::vector<Slot> slots;          // bitmap node
        bool collision = false;
        std::vector<Leaf> leaves;         // collision node
    };

    NodePtr root_;
    int count_ = 0;

    Map(NodePtr root, int count) : root_(std::move(root)), count_(count) {}

    static int popcount(std::uint32_t x) { return __builtin_popcount(x); }
    static int index(std::uint32_t bitmap, std::uint32_t bit) { return popcount(bitmap & (bit - 1)); }

    static const V* find(const Node& node, std::size_t hash, int shift, const K& key) {
        if (node.collision) {
            for (auto& l : node.leaves) if (l.key == key) return &l.value;
            return nullptr;
        }
        std::uint32_t bit = 1u << ((hash >> shift) & Mask);
        if (!(node.bitmap & bit)) return nullptr;
        const Slot& slot = node.slots[index(node.bitmap, bit)];
        if (auto* leaf = std::get_if<Leaf>(&slot)) return (leaf->hash == hash && leaf->key == key) ? &leaf->value : nullptr;
        return find(*std::get<NodePtr>(slot), hash, shift + Bits, key);
    }

    static NodePtr merge(Leaf a, Leaf b, int shift) {
        auto n = makeRc<Node>();
        if (a.hash == b.hash || shift >= 64) {
            n->collision = true;
            n->leaves = {std::move(a), std::move(b)};
            return n;
        }
        std::uint32_t ba = 1u << ((a.hash >> shift) & Mask), bb = 1u << ((b.hash >> shift) & Mask);
        if (ba == bb) {
            n->bitmap = ba;
            n->slots.push_back(merge(std::move(a), std::move(b), shift + Bits));
        } else {
            n->bitmap = ba | bb;
            if (ba < bb) { n->slots.push_back(std::move(a)); n->slots.push_back(std::move(b)); }
            else { n->slots.push_back(std::move(b)); n->slots.push_back(std::move(a)); }
        }
        return n;
    }

    static NodePtr insert(const Node& node, std::size_t hash, int shift, K key, V value, int& delta) {
        auto result = makeRc<Node>(node);
        if (node.collision) {
            for (auto& l : result->leaves) if (l.key == key) { l.value = std::move(value); delta = 0; return result; }
            result->leaves.push_back(Leaf{hash, std::move(key), std::move(value)});
            delta = 1;
            return result;
        }
        std::uint32_t bit = 1u << ((hash >> shift) & Mask);
        int idx = index(node.bitmap, bit);
        if (!(node.bitmap & bit)) {
            result->slots.insert(result->slots.begin() + idx, Leaf{hash, std::move(key), std::move(value)});
            result->bitmap |= bit;
            delta = 1;
            return result;
        }
        Slot& slot = result->slots[idx];
        if (auto* leaf = std::get_if<Leaf>(&slot)) {
            if (leaf->hash == hash && leaf->key == key) { leaf->value = std::move(value); delta = 0; }
            else { slot = merge(*leaf, Leaf{hash, std::move(key), std::move(value)}, shift + Bits); delta = 1; }
        } else slot = insert(*std::get<NodePtr>(slot), hash, shift + Bits, std::move(key), std::move(value), delta);
        return result;
    }

    static NodePtr erase(const Node& node, std::size_t hash, int shift, const K& key, int& delta) {
        auto result = makeRc<Node>(node);
        if (node.collision) {
            auto it = std::find_if(result->leaves.begin(), result->leaves.end(), [&](const Leaf& l) { return l.key == key; });
            if (it == result->leaves.end()) { delta = 0; return result; }
            result->leaves.erase(it);
            delta = -1;
            return result;
        }
        std::uint32_t bit = 1u << ((hash >> shift) & Mask);
        if (!(node.bitmap & bit)) { delta = 0; return result; }
        int idx = index(node.bitmap, bit);
        Slot& slot = result->slots[idx];
        if (auto* leaf = std::get_if<Leaf>(&slot)) {
            if (leaf->hash != hash || !(leaf->key == key)) { delta = 0; return result; }
            result->slots.erase(result->slots.begin() + idx);
            result->bitmap &= ~bit;
            delta = -1;
            return result;
        }
        auto child = erase(*std::get<NodePtr>(slot), hash, shift + Bits, key, delta);
        if (child->collision ? child->leaves.empty() : child->bitmap == 0) {
            result->slots.erase(result->slots.begin() + idx);
            result->bitmap &= ~bit;
        } else slot = child;
        return result;
    }

    template <class F> static void each(const Node& node, F& f) {
        if (node.collision) { for (auto& l : node.leaves) f(l.key, l.value); return; }
        for (auto& slot : node.slots) {
            if (auto* leaf = std::get_if<Leaf>(&slot)) f(leaf->key, leaf->value);
            else each(*std::get<NodePtr>(slot), f);
        }
    }

public:
    Map() : root_(makeRc<Node>()) {}
    int size() const { return count_; }
    bool empty() const { return count_ == 0; }

    const V* tryGet(const K& key) const { return find(*root_, std::hash<K>{}(key), 0, key); }
    bool containsKey(const K& key) const { return tryGet(key) != nullptr; }
    Option<V> get(const K& key) const { auto* v = tryGet(key); return v ? Option<V>::Some(*v) : Option<V>::None(); }
    V getOr(const K& key, V fallback) const { auto* v = tryGet(key); return v ? *v : fallback; }

    Map set(K key, V value) const {
        int delta = 0;
        const std::size_t hash = std::hash<K>{}(key);  // before the move: argument evaluation order is unspecified
        auto root = insert(*root_, hash, 0, std::move(key), std::move(value), delta);
        return Map(root, count_ + delta);
    }
    Map remove(const K& key) const {
        int delta = 0;
        auto root = erase(*root_, std::hash<K>{}(key), 0, key, delta);
        return delta == 0 ? *this : Map(root, count_ + delta);
    }

    template <class F> void forEachEntry(F f) const { each(*root_, f); }
    Vector<K> keys() const { Vector<K> r; forEachEntry([&](const K& k, const V&) { r = r.append(k); }); return r; }
    Vector<V> values() const { Vector<V> r; forEachEntry([&](const K&, const V& v) { r = r.append(v); }); return r; }

    bool operator==(const Map& o) const {
        if (o.count_ != count_) return false;
        bool same = true;
        forEachEntry([&](const K& k, const V& v) { auto* ov = o.tryGet(k); if (!ov || !(*ov == v)) same = false; });
        return same;
    }
};

// ---------------------------------------------------------------- Set

template <class T>
class Set {
    Map<T, Unit> map_;
    explicit Set(Map<T, Unit> m) : map_(std::move(m)) {}
public:
    Set() = default;
    int size() const { return map_.size(); }
    bool empty() const { return map_.empty(); }
    bool contains(const T& x) const { return map_.containsKey(x); }
    Set add(T x) const { return Set(map_.set(std::move(x), unit)); }
    Set remove(const T& x) const { return Set(map_.remove(x)); }
    Vector<T> items() const { return map_.keys(); }
    Set merge(const Set& o) const { Set r = *this; for (const auto& x : o.items()) r = r.add(x); return r; }
    Set intersect(const Set& o) const { Set r; for (const auto& x : items()) if (o.contains(x)) r = r.add(x); return r; }
    Set difference(const Set& o) const { Set r; for (const auto& x : items()) if (!o.contains(x)) r = r.add(x); return r; }
    template <class F> void forEachItem(F f) const { map_.forEachEntry([&](const T& k, const Unit&) { f(k); }); }
    bool operator==(const Set& o) const { return map_ == o.map_; }
};

// ---------------------------------------------------------------- Prelude

template <class T, class E, class F>
auto mapError(const Result<T, E>& r, F f) -> Result<T, decltype(f(*r.error))> {
    using G = decltype(f(*r.error));
    if (r.ok) return OkValue<T>{*r.value};
    return ErrorValue<G>{f(*r.error)};
}
template <class T, class E> bool isOk(const Result<T, E>& r) { return r.ok; }
template <class T, class E> bool isError(const Result<T, E>& r) { return !r.ok; }
template <class K, class V> V getOr(const Map<K, V>& m, const K& key, V fallback) { return m.getOr(key, std::move(fallback)); }
template <class T> T getOr(const Option<T>& o, T fallback) { return o.v ? *o.v : fallback; }
template <class T, class E> T getOr(const Result<T, E>& r, T fallback) { return r.ok ? *r.value : fallback; }

template <class T> Vector<T> append(const Vector<T>& v, T item) { return v.append(std::move(item)); }
template <class T> Vector<T> concat(const Vector<T>& a, const Vector<T>& b) { return a.concat(b); }
template <class T> std::int64_t length(const Vector<T>& v) { return v.size(); }
inline std::int64_t length(const std::string& s) { return static_cast<std::int64_t>(s.size()); }
template <class K, class V> std::int64_t length(const Map<K, V>& m) { return m.size(); }
template <class T> bool isEmpty(const Vector<T>& v) { return v.empty(); }
inline bool isEmpty(const std::string& s) { return s.empty(); }
template <class K, class V> bool isEmpty(const Map<K, V>& m) { return m.empty(); }

template <class T, class F> auto map(const Vector<T>& v, F f) -> Vector<decltype(f(v.get(0)))> {
    Vector<decltype(f(v.get(0)))> r;
    for (const auto& x : v) r = r.append(f(x));
    return r;
}
template <class T, class E, class F> auto map(const Result<T, E>& r, F f) -> Result<decltype(f(*r.value)), E> {
    using U = decltype(f(*r.value));
    if (r.ok) return OkValue<U>{f(*r.value)};
    return ErrorValue<E>{*r.error};
}
template <class T, class F> auto map(const Option<T>& o, F f) -> Option<decltype(f(*o.v))> {
    using U = decltype(f(*o.v));
    if (o.v) return Option<U>::Some(f(*o.v));
    return Option<U>::None();
}
template <class T, class F> Vector<T> filter(const Vector<T>& v, F f) {
    Vector<T> r;
    for (const auto& x : v) if (f(x)) r = r.append(x);
    return r;
}
template <class T, class A, class F> A fold(const Vector<T>& v, A acc, F f) {
    for (const auto& x : v) acc = f(acc, x);
    return acc;
}
template <class T, class F> bool any(const Vector<T>& v, F f) { for (const auto& x : v) if (f(x)) return true; return false; }
template <class T, class F> bool all(const Vector<T>& v, F f) { for (const auto& x : v) if (!f(x)) return false; return true; }
template <class T, class F> Option<T> find(const Vector<T>& v, F f) {
    for (const auto& x : v) if (f(x)) return Option<T>::Some(x);
    return Option<T>::None();
}
template <class T, class F> Unit forEach(const Vector<T>& v, F f) { for (const auto& x : v) f(x); return unit; }
template <class T> Option<T> first(const Vector<T>& v) { return v.empty() ? Option<T>::None() : Option<T>::Some(v.get(0)); }
template <class T> Option<T> last(const Vector<T>& v) { return v.empty() ? Option<T>::None() : Option<T>::Some(v.get(v.size() - 1)); }
template <class T> Vector<T> take(const Vector<T>& v, std::int64_t n) {
    Vector<T> r;
    for (std::int64_t i = 0; i < n && i < static_cast<std::int64_t>(v.size()); i++) r = r.append(v.get(static_cast<int>(i)));
    return r;
}
template <class T> Vector<T> drop(const Vector<T>& v, std::int64_t n) {
    Vector<T> r;
    for (std::int64_t i = std::max<std::int64_t>(0, n); i < static_cast<std::int64_t>(v.size()); i++) r = r.append(v.get(static_cast<int>(i)));
    return r;
}
template <class T> Option<T> at(const Vector<T>& v, std::int64_t i) {
    return i >= 0 && i < static_cast<std::int64_t>(v.size()) ? Option<T>::Some(v.get(static_cast<int>(i))) : Option<T>::None();
}
template <class T, class F> Vector<T> sortBy(const Vector<T>& v, F key) {
    using K = decltype(key(v.get(0)));
    std::vector<std::pair<K, T>> items;
    for (const auto& x : v) items.emplace_back(key(x), x);
    std::stable_sort(items.begin(), items.end(), [](const auto& a, const auto& b) { return a.first < b.first; });
    Vector<T> r;
    for (auto& p : items) r = r.append(p.second);
    return r;
}
template <class T, class F> Task<Vector<T>> sortByAsync(Vector<T> v, F key) {
    using K = typename decltype(key(v.get(0)))::value_type;
    std::vector<std::pair<K, T>> items;
    for (const auto& x : v) items.emplace_back(co_await key(x), x);
    std::stable_sort(items.begin(), items.end(), [](const auto& a, const auto& b) { return a.first < b.first; });
    Vector<T> r;
    for (auto& p : items) r = r.append(p.second);
    co_return r;
}
template <class T, class F> auto traverse(const Vector<T>& v, F f)
    -> Result<Vector<typename decltype(f(v.get(0)))::value_type>, typename decltype(f(v.get(0)))::error_type> {
    using R = decltype(f(v.get(0)));
    Vector<typename R::value_type> acc;
    for (const auto& x : v) {
        R r = f(x);
        if (!r.ok) return ErrorValue<typename R::error_type>{*r.error};
        acc = acc.append(*r.value);
    }
    return OkValue<Vector<typename R::value_type>>{acc};
}
template <class T, class F> auto traverseAsync(Vector<T> v, F f)
    -> Task<Result<Vector<typename decltype(f(v.get(0)))::value_type::value_type>, typename decltype(f(v.get(0)))::value_type::error_type>> {
    using R = typename decltype(f(v.get(0)))::value_type;
    Vector<typename R::value_type> acc;
    for (const auto& x : v) {
        R r = co_await f(x);
        if (!r.ok) co_return ErrorValue<typename R::error_type>{*r.error};
        acc = acc.append(*r.value);
    }
    co_return OkValue<Vector<typename R::value_type>>{acc};
}
// ---------------------------------------------------------------- Seq: lazy sequences

/// A lazy, re-iterable, possibly infinite sequence: start() yields a fresh puller that returns
/// one item per call and nullopt at the end. A Seq is a computation, not a value: no ==.
template <class T>
class Seq {
public:
    using Puller = std::function<std::optional<T>()>;
    std::function<Puller()> start;
    Seq() = default;
    explicit Seq(std::function<Puller()> s) : start(std::move(s)) {}
};

namespace seq {
template <class T> Seq<T> from(Vector<T> v) {
    return Seq<T>([v] { int i = 0; return [v, i]() mutable -> std::optional<T> { if (i < v.size()) return v.get(i++); return std::nullopt; }; });
}
template <class T, class F> Seq<T> iterate(T seed, F f) {
    return Seq<T>([seed, f] { std::optional<T> cur; return [seed, f, cur]() mutable -> std::optional<T> { cur = cur ? f(*cur) : seed; return cur; }; });
}
inline Seq<std::int64_t> range(std::int64_t from, std::int64_t to) {
    return Seq<std::int64_t>([from, to] { std::int64_t i = from; return [i, to]() mutable -> std::optional<std::int64_t> { if (i < to) return i++; return std::nullopt; }; });
}
}

template <class T, class F> auto map(const Seq<T>& s, F f) -> Seq<decltype(f(std::declval<T>()))> {
    using U = decltype(f(std::declval<T>()));
    return Seq<U>([s, f] { auto pull = s.start(); return [pull, f]() mutable -> std::optional<U> { auto x = pull(); if (!x) return std::nullopt; return f(*x); }; });
}
template <class T, class F> Seq<T> filter(const Seq<T>& s, F f) {
    return Seq<T>([s, f] { auto pull = s.start(); return [pull, f]() mutable -> std::optional<T> { for (;;) { auto x = pull(); if (!x || f(*x)) return x; } }; });
}
template <class T, class F> Seq<T> takeWhile(const Seq<T>& s, F f) {
    return Seq<T>([s, f] { auto pull = s.start(); bool done = false; return [pull, f, done]() mutable -> std::optional<T> { if (done) return std::nullopt; auto x = pull(); if (!x || !f(*x)) { done = true; return std::nullopt; } return x; }; });
}
template <class T> Seq<T> take(const Seq<T>& s, std::int64_t n) {
    return Seq<T>([s, n] { auto pull = s.start(); std::int64_t left = n; return [pull, left]() mutable -> std::optional<T> { if (left <= 0) return std::nullopt; --left; return pull(); }; });
}
template <class T> Seq<T> drop(const Seq<T>& s, std::int64_t n) {
    return Seq<T>([s, n] { auto pull = s.start(); std::int64_t skip = n; return [pull, skip]() mutable -> std::optional<T> { while (skip > 0) { --skip; if (!pull()) return std::nullopt; } return pull(); }; });
}
template <class T> Option<T> first(const Seq<T>& s) { auto pull = s.start(); auto x = pull(); return x ? Option<T>::Some(*x) : Option<T>::None(); }
template <class T> Vector<T> toVector(const Seq<T>& s) { Vector<T> r; auto pull = s.start(); while (auto x = pull()) r = r.append(*x); return r; }
template <class T, class A, class F> A fold(const Seq<T>& s, A acc, F f) { auto pull = s.start(); while (auto x = pull()) acc = f(acc, *x); return acc; }

// The built-in Ord instances. A generated instance is a struct with the same shape.
struct Ord_Int { std::int64_t compare(std::int64_t a, std::int64_t b) const { return a < b ? -1 : (a > b ? 1 : 0); } };
struct Ord_Float { std::int64_t compare(double a, double b) const { return a < b ? -1 : (a > b ? 1 : 0); } };
struct Ord_String { std::int64_t compare(const std::string& a, const std::string& b) const { return a < b ? -1 : (a > b ? 1 : 0); } };
struct Ord_Bool { std::int64_t compare(bool a, bool b) const { return a == b ? 0 : (a ? 1 : -1); } };
struct Ord_Instant { std::int64_t compare(const Instant& a, const Instant& b) const { return a < b ? -1 : (a > b ? 1 : 0); } };
template <class T, class O> Vector<T> sort(const Vector<T>& v, O ord) {
    std::vector<T> items;
    for (const auto& x : v) items.push_back(x);
    std::stable_sort(items.begin(), items.end(), [&](const T& a, const T& b) { return ord.compare(a, b) < 0; });
    Vector<T> r;
    for (auto& x : items) r = r.append(x);
    return r;
}
template <class T, class O> Option<T> maximum(const Vector<T>& v, O ord) {
    if (v.empty()) return Option<T>::None();
    T best = v.get(0);
    for (const auto& x : v) if (ord.compare(x, best) > 0) best = x;
    return Option<T>::Some(best);
}
template <class T, class O> Option<T> minimum(const Vector<T>& v, O ord) {
    if (v.empty()) return Option<T>::None();
    T best = v.get(0);
    for (const auto& x : v) if (ord.compare(x, best) < 0) best = x;
    return Option<T>::Some(best);
}
template <class T> Vector<T> reverse(const Vector<T>& v) { Vector<T> r; for (int i = v.size() - 1; i >= 0; i--) r = r.append(v.get(i)); return r; }
template <class T> bool contains(const Vector<T>& v, const T& item) { for (const auto& x : v) if (x == item) return true; return false; }
template <class K, class V> bool contains(const Map<K, V>& m, const K& key) { return m.containsKey(key); }
inline bool contains(const std::string& s, const std::string& sub) { return s.find(sub) != std::string::npos; }
inline std::int64_t sum(const Vector<std::int64_t>& v) { std::int64_t t = 0; for (auto x : v) t += x; return t; }

// ---- higher-order functions over suspending lambdas (generated code appends Async when a lambda suspends)
template <class T, class F> auto mapAsync(Vector<T> v, F f) -> Task<Vector<typename decltype(f(v.get(0)))::value_type>> {
    Vector<typename decltype(f(v.get(0)))::value_type> r;
    for (int i = 0; i < v.size(); i++) r = r.append(co_await f(v.get(i)));
    co_return r;
}
template <class T, class F> Task<Vector<T>> filterAsync(Vector<T> v, F f) {
    Vector<T> r;
    for (int i = 0; i < v.size(); i++) if (co_await f(v.get(i))) r = r.append(v.get(i));
    co_return r;
}
template <class T, class A, class F> Task<A> foldAsync(Vector<T> v, A acc, F f) {
    for (int i = 0; i < v.size(); i++) acc = co_await f(acc, v.get(i));
    co_return acc;
}
template <class T, class F> Task<bool> anyAsync(Vector<T> v, F f) {
    for (int i = 0; i < v.size(); i++) if (co_await f(v.get(i))) co_return true;
    co_return false;
}
template <class T, class F> Task<bool> allAsync(Vector<T> v, F f) {
    for (int i = 0; i < v.size(); i++) if (!co_await f(v.get(i))) co_return false;
    co_return true;
}
template <class T, class F> Task<Option<T>> findAsync(Vector<T> v, F f) {
    for (int i = 0; i < v.size(); i++) if (co_await f(v.get(i))) co_return Option<T>::Some(v.get(i));
    co_return Option<T>::None();
}
template <class T, class F> Task<Unit> forEachAsync(Vector<T> v, F f) {
    for (int i = 0; i < v.size(); i++) co_await f(v.get(i));
    co_return unit;
}
template <class T, class E, class F> auto mapErrorAsync(Result<T, E> r, F f) -> Task<Result<T, typename decltype(f(*r.error))::value_type>> {
    using G = typename decltype(f(*r.error))::value_type;
    if (r.ok) co_return OkValue<T>{*r.value};
    co_return ErrorValue<G>{co_await f(*r.error)};
}
template <class T, class F> Task<Unit> updateAsync(Cell<T> c, F f) { c.set(co_await f(c.get())); co_return unit; }

template <class T> T get(const Cell<T>& c) { return c.get(); }
template <class K, class V> Option<V> get(const Map<K, V>& m, const K& key) { return m.get(key); }
template <class T> Unit set(const Cell<T>& c, T v) { return c.set(std::move(v)); }
template <class K, class V> Map<K, V> set(const Map<K, V>& m, K key, V value) { return m.set(std::move(key), std::move(value)); }
template <class K, class V> Map<K, V> remove(const Map<K, V>& m, const K& key) { return m.remove(key); }
template <class T> Set<T> remove(const Set<T>& s, const T& x) { return s.remove(x); }
template <class T> std::int64_t length(const Set<T>& s) { return s.size(); }
template <class T> bool isEmpty(const Set<T>& s) { return s.empty(); }
template <class T> bool contains(const Set<T>& s, const T& x) { return s.contains(x); }
template <class T> Set<T> toSet(const Vector<T>& v) { Set<T> s; for (const auto& x : v) s = s.add(x); return s; }
template <class T> Set<T> add(const Set<T>& s, T x) { return s.add(std::move(x)); }
template <class T> Vector<T> items(const Set<T>& s) { return s.items(); }
template <class T> Set<T> merge(const Set<T>& a, const Set<T>& b) { return a.merge(b); }
template <class T> Set<T> intersect(const Set<T>& a, const Set<T>& b) { return a.intersect(b); }
template <class T> Set<T> difference(const Set<T>& a, const Set<T>& b) { return a.difference(b); }
// the emitter escapes 'remove' (it collides with <cstdio>), so the prelude also answers to remove_
template <class K, class V> Map<K, V> remove_(const Map<K, V>& m, const K& key) { return m.remove(key); }
template <class T> Set<T> remove_(const Set<T>& s, const T& x) { return s.remove(x); }
template <class K, class V> Vector<K> keys(const Map<K, V>& m) { return m.keys(); }
template <class K, class V> Vector<V> values(const Map<K, V>& m) { return m.values(); }
template <class T, class F> Unit update(const Cell<T>& c, F f) { return c.update(f); }
template <class T, class F> T getAndUpdate(const Cell<T>& c, F f) { return c.getAndUpdate(f); }

inline std::string trim(const std::string& s) {
    auto b = s.find_first_not_of(" \t\r\n"), e = s.find_last_not_of(" \t\r\n");
    return b == std::string::npos ? "" : s.substr(b, e - b + 1);
}
inline std::string toUpper(std::string s) { for (auto& c : s) c = static_cast<char>(std::toupper(static_cast<unsigned char>(c))); return s; }
inline std::string toLower(std::string s) { for (auto& c : s) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c))); return s; }
inline bool startsWith(const std::string& s, const std::string& p) { return s.rfind(p, 0) == 0; }
inline std::string uuid() {
    static std::mt19937_64 rng{std::random_device{}()};
    static const char* hex = "0123456789abcdef";
    std::string s;
    for (int i = 0; i < 32; i++) {
        if (i == 8 || i == 12 || i == 16 || i == 20) s += '-';
        s += hex[rng() & 15];
    }
    return s;
}

template <class T> std::string toString(const T&) { return "<value>"; }
inline std::string toString(const std::string& s) { return s; }
inline std::string toString(std::int64_t v) { return std::to_string(v); }
inline std::string toString(int v) { return std::to_string(v); }
inline std::string toString(double v) { std::ostringstream o; o << v; return o.str(); }
inline std::string toString(bool b) { return b ? "true" : "false"; }
inline std::string toString(const Instant& i) { return i.toString(); }
inline std::string toString(Unit) { return "unit"; }
template <class... Ts> std::string toString(const std::tuple<Ts...>& t) {
    std::string s = "(";
    bool firstItem = true;
    std::apply([&](const auto&... xs) { ((s += (firstItem ? "" : ", ") + toString(xs), firstItem = false), ...); }, t);
    return s + ")";
}
template <class T> std::string toString(const Vector<T>& v) {
    std::string s = "[";
    bool firstItem = true;
    for (const auto& x : v) { if (!firstItem) s += ", "; s += toString(x); firstItem = false; }
    return s + "]";
}
template <class T> Unit print(const T& v) { std::cout << toString(v) << std::endl; return unit; }

namespace sys { inline Instant clock() { return Instant::now(); } }
namespace env {
    inline std::string get(const std::string& name) {
        const char* v = std::getenv(name.c_str());
        if (!v) throw Panic("env.get: " + name + " is not set");
        return v;
    }
}
namespace json {
    template <class T> std::string encode(const T& v) { return toString(v); }
    template <class T> T decode(const std::string&) { throw Panic("json.decode is not available in the C++ runtime yet"); }
}

}  // namespace treb

// ---------------------------------------------------------------- hashes

template <> struct std::hash<treb::Unit> { std::size_t operator()(const treb::Unit&) const { return 1; } };
template <> struct std::hash<treb::Instant> { std::size_t operator()(const treb::Instant& i) const { return std::hash<std::int64_t>{}(i.millis); } };
template <class T> struct std::hash<treb::Vector<T>> {
    std::size_t operator()(const treb::Vector<T>& v) const { std::size_t h = 17; for (const auto& x : v) h = treb::hashCombine(h, x); return h; }
};
template <class... Ts> struct std::hash<std::tuple<Ts...>> {
    std::size_t operator()(const std::tuple<Ts...>& t) const {
        std::size_t h = 31;
        std::apply([&](const auto&... xs) { ((h = treb::hashCombine(h, xs)), ...); }, t);
        return h;
    }
};
template <class T> struct std::hash<treb::Box<T>> {
    std::size_t operator()(const treb::Box<T>& b) const { return std::hash<T>{}(*b); }
};
template <class T> struct std::hash<treb::Set<T>> {
    std::size_t operator()(const treb::Set<T>& s) const { std::size_t h = 0; s.forEachItem([&](const T& x) { h ^= std::hash<T>{}(x); }); return treb::hashCombine(19, h); }
};
template <class K, class V> struct std::hash<treb::Map<K, V>> {
    std::size_t operator()(const treb::Map<K, V>& m) const { std::size_t h = 0; m.forEachEntry([&](const K& k, const V& v) { h ^= treb::hashCombine(23, k, v); }); return treb::hashCombine(29, h); }
};
template <class T> struct std::hash<treb::Option<T>> {
    std::size_t operator()(const treb::Option<T>& o) const { return o.v ? treb::hashCombine(3, *o.v) : 5; }
};
template <class T, class E> struct std::hash<treb::Result<T, E>> {
    std::size_t operator()(const treb::Result<T, E>& r) const { return r.ok ? treb::hashCombine(7, *r.value) : treb::hashCombine(11, *r.error); }
};
template <> struct std::hash<treb::PanicValue> { std::size_t operator()(const treb::PanicValue& e) const { return std::hash<std::string>{}(e.message); } };
template <> struct std::hash<treb::ArgumentError> { std::size_t operator()(const treb::ArgumentError& e) const { return std::hash<std::string>{}(e.message); } };
