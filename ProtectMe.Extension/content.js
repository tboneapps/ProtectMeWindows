(() => {
  const style = document.createElement('style');
  style.id = 'protectme-safe-browsing-style';
  style.textContent = `
    img, picture, video, canvas, svg[role="img"] {
      filter: brightness(0) !important;
      background-color: #000000 !important;
      pointer-events: none !important;
    }
    video {
      opacity: 1 !important;
    }
  `;
  (document.head || document.documentElement).appendChild(style);

  // Prevent any video from playing
  const stopVideos = () => {
    document.querySelectorAll('video').forEach(v => {
      try { v.pause(); v.removeAttribute('src'); v.load(); } catch {}
    });
  };
  document.addEventListener('DOMContentLoaded', stopVideos);
  setInterval(stopVideos, 1000);
})();
