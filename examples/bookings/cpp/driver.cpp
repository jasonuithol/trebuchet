// Test driver for the C++ backend. Runs the same scenario as the interpreter test and
// the C# emitter test against the generated code, printing one line per step.
// Build: g++ -std=c++20 -I<runtime dir> -I<generated dir> driver.cpp -o driver
// Suspending API methods return treb::Task; get() runs each on the event loop.
#include <string>
namespace host { std::string hostName(); }   // the extern in bookings.api.system
#include "generated.hpp"
#include <iostream>

using namespace gen;
using namespace treb;

namespace host { std::string hostName() { return "test-host"; } }

static const char* kind(bool ok) { return ok ? "Ok" : "Error"; }

template <class T>
static std::string errorName(const Result<T, ApiError>& r) {
    if (r.ok) return "";
    const ApiError& e = *r.error;
    if (std::holds_alternative<BadRequest>(e)) return "BadRequest";
    if (std::holds_alternative<NotFound>(e)) return "NotFound";
    if (std::holds_alternative<Internal>(e)) return "Internal";
    return "Conflict";
}

int main() {
    auto root = Bookings_Host_Test::test();
    auto& api = *root.api;
    auto req = [](const char* room, const char* guest, const char* start, const char* end, Vector<std::string> attendees = {}, bool waitlist = false) {
        return BookingRequest{room, guest, Instant::parse(start), Instant::parse(end), attendees, waitlist};
    };

    auto first = api.postBooking(req("boardroom", "alice", "2026-09-05T10:00:00Z", "2026-09-05T11:00:00Z")).get();
    std::cout << "first=" << kind(first.ok) << " " << (first.ok ? first.value->id : errorName(first)) << "\n";
    auto second = api.postBooking(req("boardroom", "bob", "2026-09-05T10:30:00Z", "2026-09-05T11:30:00Z")).get();
    std::cout << "second=" << kind(second.ok) << " " << errorName(second) << "\n";
    auto past = api.postBooking(req("boardroom", "carol", "2026-09-05T07:00:00Z", "2026-09-05T08:00:00Z")).get();
    std::cout << "past=" << kind(past.ok) << " " << errorName(past) << "\n";
    auto missing = api.postBooking(req("attic", "dave", "2026-09-05T10:00:00Z", "2026-09-05T11:00:00Z")).get();
    std::cout << "missing=" << kind(missing.ok) << " " << errorName(missing) << "\n";
    Vector<std::string> five = Vector<std::string>{}.append("a").append("b").append("c").append("d").append("e");
    auto crowd = api.postBooking(req("huddle", "eve", "2026-09-05T12:00:00Z", "2026-09-05T13:00:00Z", five)).get();
    std::cout << "crowd=" << kind(crowd.ok) << " " << errorName(crowd) << "\n";
    auto waiting = api.postBooking(req("boardroom", "bob", "2026-09-05T10:30:00Z", "2026-09-05T11:30:00Z", {}, true)).get();
    std::cout << "waiting=" << kind(waiting.ok) << " " << (waiting.ok ? waiting.value->id : errorName(waiting)) << "\n";

    auto confirmed = api.postConfirm("boardroom", "bk-1").get();
    std::cout << "confirmed=" << kind(confirmed.ok) << "\n";
    auto cancelled = api.deleteBooking("boardroom", "bk-1", "plans changed").get();
    std::cout << "cancelled=" << kind(cancelled.ok) << "\n";
    auto again = api.deleteBooking("boardroom", "bk-1", "again").get();
    std::cout << "again=" << kind(again.ok) << " " << errorName(again) << "\n";

    auto list = api.getBookings("boardroom").get();
    std::string promoted = "?";
    if (list.ok) for (const auto& b : *list.value) if (b.id.value == "bk-6") promoted = Bookings_Domain_Queries::statusName(b.status);
    std::cout << "promoted=" << promoted << "\n";

    auto avail = api.getAvailability("boardroom", Instant::parse("2026-09-05T12:00:00Z"), Instant::parse("2026-09-05T13:00:00Z")).get();
    std::cout << "free=" << kind(avail.ok) << " " << (avail.ok && *avail.value ? "true" : "false") << "\n";
    std::cout << "count=" << (list.ok ? list.value->size() : -1) << "\n";

    auto page = api.getPage("boardroom", 0, 1).get();
    std::cout << "page=" << (page.ok ? page.value->items.size() : -1) << "/" << (page.ok ? page.value->total : -1) << "\n";

    auto stats = api.getStats().get();
    if (stats.ok) for (const auto& s : *stats.value) if (s.room == "boardroom")
        std::cout << "stats=" << s.room << " held=" << s.held << " cancelled=" << s.cancelled << " guests=" << s.guests << "\n";

    auto info = api.getInfo().get();
    std::cout << "info=" << (info.ok ? info.value->rooms : -1) << " rooms on " << (info.ok ? info.value->host : "?") << "\n";
    return 0;
}
