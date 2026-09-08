// Host functions must be declared before the generated header is included.
#include <fstream>
#include <iterator>
#include <sstream>
#include <string>
#include "trebuchet.hpp"
namespace host {
    std::string readTextFile(std::string path);
    treb::Vector<std::string> readLines(std::string path);
    std::string urlEncode(std::string s);
}
#include "generated.hpp"
#include <iostream>

namespace host {
    std::string readTextFile(std::string path) {
        std::ifstream f(path);
        if (!f) throw std::ios_base::failure("cannot open " + path);
        return std::string(std::istreambuf_iterator<char>(f), std::istreambuf_iterator<char>());
    }
    treb::Vector<std::string> readLines(std::string path) {
        std::ifstream f(path);
        if (!f) throw std::ios_base::failure("cannot open " + path);
        treb::Vector<std::string> lines;
        std::string line;
        while (std::getline(f, line)) lines = lines.append(line);
        return lines;
    }
    std::string urlEncode(std::string s) {
        std::string out;
        for (unsigned char c : s) {
            if (std::isalnum(c)) out += static_cast<char>(c);
            else if (c == ' ') out += '+';
            else { char buf[4]; std::snprintf(buf, sizeof buf, "%%%02X", c); out += buf; }
        }
        return out;
    }
}

int main(int argc, char** argv) {
    if (argc < 3) return 2;
    std::cout << gen::Ffi::demo(argv[1], argv[2]).get() << "\n";
    return 0;
}
