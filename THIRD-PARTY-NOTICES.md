# Third-party notices

## TITAN (titan-ultima)

`src/U9.Formats/Text/MicroTalk.cs` (the EA MicroTalk speech decoder) is a C# port of `titan/u9/microtalk.py` from
theGreyWanderer's TITAN toolkit, https://github.com/theGreyWanderer-uc/tgwUltima (titan-ultima). TITAN's decoder is
itself a port of vgmstream's `utkdec`, which derives from Andrew D'Addesio's public-domain `utkencode`
(https://github.com/daddesio/utkencode). The Speech.flx record layout and codec numbers follow TITAN's
`titan/u9/sound.py`.

```
MIT License

Copyright (c) 2025 theGreyWanderer

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
