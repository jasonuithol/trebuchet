#include "generated.hpp"
#include <iostream>
int main() {
    auto entries = gen::Resources::demo();
    for (int i = 0; i < entries.size(); i++) std::cout << entries.get(i) << (i + 1 < entries.size() ? ", " : "\n");
    return 0;
}
