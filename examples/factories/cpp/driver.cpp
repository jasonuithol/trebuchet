#include "generated.hpp"
#include <iostream>
int main() {
    auto root = gen::Factories::main_();
    // separate statements: C++ does not order operands within one expression
    std::string a = root.client->ping("a");
    std::string b = root.client->ping("b");
    std::string out = a + " " + b + " |";
    for (const auto& e : root.client->history()) out += " " + e;
    std::cout << out << "\n";
    return 0;
}
