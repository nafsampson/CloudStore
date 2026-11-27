//summary//
// Compiles MayBell utility classes and CloudStore's semantic view components into a local stylesheet.
// Scans Razor and JavaScript sources so no runtime CSS service or CDN is required.
//summary//
//-------------------Start Of File------------------//
module.exports = {
  content: ['./Views/**/*.cshtml', './wwwroot/js/storefront.js'],
  theme: {
    extend: {
      colors: { store: { purple: '#4c1d95', dark: '#2e1065', amber: '#fbbf24' } },
      fontFamily: { sans: ['Segoe UI Variable Text', 'Segoe UI', 'system-ui', 'sans-serif'] }
    }
  },
  plugins: []
};
//-------------------End Of File------------------//
