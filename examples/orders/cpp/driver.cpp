// The milestone-6 acceptance scenario on C++: one command, one handler, one query, one
// service, one root. Prints the same line as the interpreter and C# tests.
#include "generated.hpp"
#include <iostream>
using namespace gen::Orders;
using namespace treb;

static std::string errorName(const Result<Unit, OrderError>& r) {
    if (r.ok) return "Ok";
    return std::holds_alternative<AlreadyPlaced>(*r.error) ? "AlreadyPlaced" : "EmptyOrder";
}

int main() {
    auto root = main_();
    auto& svc = *root.orderService;
    OrderId id{"o-1"};
    CustomerId customer{"alice"};
    Vector<Line> lines = Vector<Line>{}.append(Line{"a", 2, 100}).append(Line{"b", 1, 150});
    std::string placed = errorName(svc.place(PlaceOrder{id, customer, lines}).get());
    std::string again = errorName(svc.place(PlaceOrder{id, customer, lines}).get());
    std::string empty = errorName(svc.place(PlaceOrder{OrderId{"o-2"}, customer, Vector<Line>{}}).get());
    auto described = svc.describe(id, customer).get();
    std::cout << "placed=" << placed << " again=" << again << " empty=" << empty << " " << (described.ok ? *described.value : "?") << "\n";
    return 0;
}
