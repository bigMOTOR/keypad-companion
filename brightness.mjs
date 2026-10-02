export const defaultPoints=[{screen:0,keypad:10},{screen:25,keypad:16},{screen:40,keypad:20},{screen:50,keypad:23},{screen:75,keypad:31},{screen:100,keypad:40}];
export function validatePoints(input){
  if(!Array.isArray(input)||input.length<2||input.length>20)throw new Error('Потрібно від 2 до 20 точок.');
  const points=input.map(p=>({screen:p.screen,keypad:p.keypad})).sort((a,b)=>a.screen-b.screen);
  if(points.some(p=>!Number.isInteger(p.screen)||p.screen<0||p.screen>100||!Number.isInteger(p.keypad)||p.keypad<1||p.keypad>100))throw new Error('Екран: 0–100%. Keypad: 1–100%. Введи цілі числа.');
  if(points[0].screen!==0||points.at(-1).screen!==100)throw new Error('Залиш точки для екрана 0% і 100%.');
  if(points.some((p,i)=>i>0&&p.screen===points[i-1].screen))throw new Error('Значення екрана в різних точках мають відрізнятися.');
  return points;
}
export function mapBrightness(screen,points){
  const x=Math.max(0,Math.min(100,screen));
  for(let i=1;i<points.length;i++){const a=points[i-1],b=points[i];if(x<=b.screen)return Math.round(a.keypad+(b.keypad-a.keypad)*(x-a.screen)/(b.screen-a.screen));}
  return points.at(-1).keypad;
}
